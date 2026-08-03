using System.Diagnostics;
using System.Text;

namespace FindFamiliar.Runner;

public sealed record AdapterExecutionResult(
    bool TimedOut,
    bool LaunchFailed,
    int? ExitCode,
    byte[] StdoutBytes,
    bool StdoutOversized,
    byte[] StderrBytes,
    bool StderrOversized);

/// <summary>
/// Launches the administrator-configured adapter executable directly — no shell, no command-line
/// interpolation of task/session/prompt/context content. Stdout and stderr are drained
/// concurrently (never sequentially after exit) to avoid the classic redirected-pipe deadlock,
/// and both are bounded so a runaway adapter cannot exhaust memory.
/// </summary>
public sealed class AdapterProcessExecutor
{
    public async Task<AdapterExecutionResult> RunAsync(
        string adapterPath,
        IReadOnlyList<string> adapterArguments,
        string stdinJson,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = adapterPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in adapterArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The adapter never needs the Familiar bearer credential; remove it explicitly rather
        // than relying on the adapter to ignore an inherited secret.
        startInfo.Environment.Remove(RunnerArguments.TokenVariable);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = false };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new AdapterExecutionResult(false, true, null, [], false, [], false);
        }

        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, RunnerProtocol.MaxAdapterOutputBytes, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, RunnerProtocol.MaxAdapterOutputBytes, cancellationToken);

        // The timeout clock starts here, before the stdin write, and covers the write itself —
        // not just the later WaitForExitAsync. An adapter that never reads stdin (assignment
        // Markdown can be up to 500,000 characters, far larger than a typical OS pipe buffer)
        // would otherwise block this write forever, and the process-tree kill below would never
        // run.
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.StandardInput.WriteAsync(stdinJson.AsMemory(), linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process may already be gone after a timeout kill — closing its stdin is
                // then a no-op we can safely ignore.
            }
        }

        if (!timedOut)
        {
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                timedOut = true;
                TryKillProcessTree(process);
            }
        }

        var (stdoutBytes, stdoutOversized) = await stdoutTask;
        var (stderrBytes, stderrOversized) = await stderrTask;

        return new AdapterExecutionResult(
            timedOut,
            false,
            timedOut ? null : process.ExitCode,
            stdoutBytes,
            stdoutOversized,
            stderrBytes,
            stderrOversized);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout firing and the kill call — nothing to do.
        }
    }

    private static async Task<(byte[] Bytes, bool Oversized)> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var oversized = false;

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            if (!oversized)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                {
                    oversized = true;
                }
            }
        }

        return (oversized ? [] : buffer.ToArray(), oversized);
    }
}
