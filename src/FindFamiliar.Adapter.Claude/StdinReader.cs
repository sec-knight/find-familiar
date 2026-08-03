using System.Text;

namespace FindFamiliar.Adapter.Claude;

public enum StdinReadOutcome
{
    Complete,
    Oversized,
    TimedOut
}

/// <summary>
/// Reads the adapter invocation from stdin under a hard byte cap and a wall clock. Reading the
/// stream to the end first and checking its size afterwards would be no bound at all: a caller
/// that writes indefinitely, or opens the pipe and never closes it, would exhaust memory or hang
/// before any validation ran.
/// </summary>
public static class StdinReader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static async Task<(StdinReadOutcome Outcome, string Text)> ReadAsync(
        Stream stream,
        int maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, linkedCts.Token);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maxBytes)
                {
                    return (StdinReadOutcome.Oversized, string.Empty);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return (StdinReadOutcome.TimedOut, string.Empty);
        }

        return (StdinReadOutcome.Complete, Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
