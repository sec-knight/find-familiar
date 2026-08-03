using FindFamiliar.Runner;

var arguments = RunnerArguments.TryParse(args, Environment.GetEnvironmentVariables(), Console.Error);
if (arguments is null)
{
    return (int)RunnerExitCode.UsageError;
}

using var httpClient = new HttpClient
{
    BaseAddress = arguments.BaseUrl,
    Timeout = arguments.Timeout + TimeSpan.FromSeconds(60)
};

var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), Console.Error);
var exitCode = await engine.RunAsync(arguments, CancellationToken.None);

return (int)exitCode;
