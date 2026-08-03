using FindFamiliar.Adapter.Claude;
using FindFamiliar.Runner;

if (args.Length > 0)
{
    await Console.Error.WriteLineAsync("adapter: this adapter takes no arguments; all configuration is local environment.");
    return (int)ClaudeAdapterExitCode.ConfigurationInvalid;
}

var (readOutcome, stdin) = await StdinReader.ReadAsync(
    Console.OpenStandardInput(),
    InvocationValidator.MaxStdinBytes,
    StdinReader.DefaultTimeout,
    CancellationToken.None);

if (readOutcome != StdinReadOutcome.Complete)
{
    await Console.Error.WriteLineAsync($"adapter: invocation rejected (stdin {readOutcome}).");
    return (int)ClaudeAdapterExitCode.InvocationInvalid;
}

var engine = new ClaudeAdapterEngine(new AdapterProcessExecutor(), Console.Error);
var exitCode = await engine.RunAsync(stdin, Environment.GetEnvironmentVariables(), Console.Out, CancellationToken.None);

await Console.Out.FlushAsync();

return (int)exitCode;
