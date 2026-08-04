using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// Collection-scoped test host: real SQLite provider, real migrations, a unique temporary
/// file-backed database per test run. Never touches src/FindFamiliar.Server/App_Data.
/// </summary>
public sealed class FindFamiliarWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string DataDirectoryVariable = "Familiar__DataDirectory";
    private const string ConnectionStringVariable = "ConnectionStrings__FindFamiliar";
    private const string RunnerBridgeTokenVariable = "RunnerBridge__Token";

    /// <summary>
    /// Obviously-fake configured runner bridge credential, used only by tests. Never a real
    /// secret; deliberately labeled so it cannot be mistaken for one.
    /// </summary>
    public const string RunnerBridgeTestToken = "ffa-test-fixture-runner-bridge-token-not-a-real-secret";

    private readonly string? _previousDataDirectory;
    private readonly string? _previousConnectionString;
    private readonly string? _previousRunnerBridgeToken;

    public string TempDirectory { get; }

    public string RepositoryRoot { get; }

    public IReadOnlyDictionary<string, ProductionDatabaseFileSnapshot> ProductionDatabaseFilesBeforeHost { get; }

    public FindFamiliarWebApplicationFactory()
    {
        RepositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Could not locate the repository root.");
        ProductionDatabaseFilesBeforeHost = CaptureProductionDatabaseFiles(RepositoryRoot);

        TempDirectory = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectory);

        _previousDataDirectory = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        _previousRunnerBridgeToken = Environment.GetEnvironmentVariable(RunnerBridgeTokenVariable);

        Environment.SetEnvironmentVariable(DataDirectoryVariable, TempDirectory);
        Environment.SetEnvironmentVariable(
            ConnectionStringVariable,
            $"Data Source={Path.Combine(TempDirectory, "find-familiar-test.db")}");
        Environment.SetEnvironmentVariable(RunnerBridgeTokenVariable, RunnerBridgeTestToken);

        // Force the host (and Program.cs's top-level statements, including SQLitePCL
        // provider selection and the real startup migration) to run now, before any
        // test in the collection executes.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        Environment.SetEnvironmentVariable(DataDirectoryVariable, _previousDataDirectory);
        Environment.SetEnvironmentVariable(ConnectionStringVariable, _previousConnectionString);
        Environment.SetEnvironmentVariable(RunnerBridgeTokenVariable, _previousRunnerBridgeToken);

        // base.Dispose above tears down the host (and with it the DbContext pool); the shared
        // helper then releases SQLite's pooled handles before deleting, so a Windows teardown
        // cannot fail a run whose assertions all passed.
        TemporaryDirectoryCleanup.Delete(TempDirectory);
    }

    public static IReadOnlyDictionary<string, ProductionDatabaseFileSnapshot> CaptureProductionDatabaseFiles(
        string repositoryRoot)
    {
        var productionDataDirectory = Path.Combine(repositoryRoot, "src", "FindFamiliar.Server", "App_Data");
        if (!Directory.Exists(productionDataDirectory))
        {
            return new Dictionary<string, ProductionDatabaseFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.GetFiles(productionDataDirectory, "*.db", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(productionDataDirectory, path),
                path => new ProductionDatabaseFileSnapshot(
                    new FileInfo(path).Length,
                    File.GetLastWriteTimeUtc(path)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, "FindFamiliar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

public sealed record ProductionDatabaseFileSnapshot(long Length, DateTime LastWriteUtc);
