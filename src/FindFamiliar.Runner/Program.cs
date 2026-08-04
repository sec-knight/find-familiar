using FindFamiliar.Runner;

// Two entry points, one execution path. "worker" polls for claims; the original explicit
// --task-id/--session-id invocation is unchanged and still supported.
if (args.Length > 0 && args[0] == "worker")
{
    return await RunWorkerAsync(args[1..]);
}

return await RunExplicitInvocationAsync(args);

static async Task<int> RunExplicitInvocationAsync(string[] args)
{
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
}

static async Task<int> RunWorkerAsync(string[] args)
{
    if (args.Length > 0)
    {
        Console.Error.WriteLine("worker: usage: worker (configuration comes from FAMILIAR_WORKER_CONFIG).");
        return (int)RunnerExitCode.UsageError;
    }

    var configuration = WorkerConfiguration.TryLoad(Environment.GetEnvironmentVariables(), Console.Error);
    if (configuration is null)
    {
        return (int)RunnerExitCode.UsageError;
    }

    using var httpClient = new HttpClient
    {
        BaseAddress = configuration.BaseUrl,
        Timeout = configuration.AdapterTimeout + TimeSpan.FromSeconds(60)
    };

    // Ctrl+C and SIGTERM both request a clean stop: the current adapter run is cancelled through
    // the same token the engine already honors, and the loop exits rather than being killed
    // mid-execution.
    using var shutdown = new CancellationTokenSource();

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        Console.Error.WriteLine("worker: shutdown requested (Ctrl+C).");
        shutdown.Cancel();
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        Console.Error.WriteLine("worker: shutdown requested (process exit).");
        shutdown.Cancel();
    };

    var engine = new RunnerEngine(httpClient, new AdapterProcessExecutor(), Console.Error);
    var loop = new WorkerLoop(httpClient, engine, configuration, Console.Error, TimeProvider.System);

    var exitCode = await loop.RunAsync(shutdown.Token);

    return (int)exitCode;
}
