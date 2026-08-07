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
    /// Blanked for every test run, so no test can ever reach a paid conversational endpoint.
    ///
    /// Not a precaution against a test that tries — none does — but against the ambient environment.
    /// The talk lane is selected by configuration, and configuration comes partly from environment
    /// variables, so a suite run from a shell that had sourced the deployment's EnvironmentFile would
    /// otherwise inherit a real provider and a real key and spend real money on assertions. Blanking
    /// it here means the answer does not depend on who ran the tests or from where.
    /// </summary>
    private const string ChatProviderVariable = "Familiar__Chat__Provider";

    private const string ChatApiKeyVariable = "Familiar__Chat__ApiKeyVariable";

    private const string GatewayEnabledVariable = "FamiliarGateway__Enabled";
    private const string GatewayTokenVariable = "FamiliarGateway__Token";
    private const string GatewayIdentityNameVariable = "Familiar__Identity__Name";

    /// <summary>
    /// Obviously-fake configured runner bridge credential, used only by tests. Never a real
    /// secret; deliberately labeled so it cannot be mistaken for one.
    /// </summary>
    public const string RunnerBridgeTestToken = "ffa-test-fixture-runner-bridge-token-not-a-real-secret";

    /// <summary>
    /// The gateway credential for the test host. A second obviously-fake token rather than a reuse of
    /// the runner one, because the two are separate credentials in the product and a fixture that
    /// shared them would let a test pass that the deployment would fail.
    /// </summary>
    public const string GatewayTestToken = "ffa-test-fixture-familiar-gateway-token-not-a-real-secret";

    /// <summary>
    /// The identity the test host reports. Not "Sakura": a fixture that used the operator's own
    /// Familiar name would let an assertion pass on a hard-coded default rather than on configuration
    /// actually being read.
    /// </summary>
    public const string GatewayTestIdentityName = "Testwarden";

    private readonly string? _previousDataDirectory;
    private readonly string? _previousConnectionString;
    private readonly string? _previousRunnerBridgeToken;
    private readonly string? _previousChatProvider;
    private readonly string? _previousChatApiKeyVariable;
    private readonly string? _previousGatewayEnabled;
    private readonly string? _previousGatewayToken;
    private readonly string? _previousGatewayIdentityName;

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
        _previousChatProvider = Environment.GetEnvironmentVariable(ChatProviderVariable);
        _previousChatApiKeyVariable = Environment.GetEnvironmentVariable(ChatApiKeyVariable);
        _previousGatewayEnabled = Environment.GetEnvironmentVariable(GatewayEnabledVariable);
        _previousGatewayToken = Environment.GetEnvironmentVariable(GatewayTokenVariable);
        _previousGatewayIdentityName = Environment.GetEnvironmentVariable(GatewayIdentityNameVariable);

        // The Summoning Gate is on for the test host, because the properties worth protecting are
        // about what it refuses, and a gate that is not mapped refuses everything for the wrong
        // reason. The gateway-disabled case has its own fixture.
        Environment.SetEnvironmentVariable(GatewayEnabledVariable, "true");
        Environment.SetEnvironmentVariable(GatewayTokenVariable, GatewayTestToken);
        Environment.SetEnvironmentVariable(GatewayIdentityNameVariable, GatewayTestIdentityName);

        Environment.SetEnvironmentVariable(DataDirectoryVariable, TempDirectory);
        Environment.SetEnvironmentVariable(
            ConnectionStringVariable,
            $"Data Source={Path.Combine(TempDirectory, "find-familiar-test.db")}");
        Environment.SetEnvironmentVariable(RunnerBridgeTokenVariable, RunnerBridgeTestToken);

        // Both, not just the provider: IsConfigured() needs a selected provider *and* a resolvable
        // key, so clearing either is sufficient — and clearing both means a future change to that
        // rule cannot quietly re-open the door.
        Environment.SetEnvironmentVariable(ChatProviderVariable, string.Empty);
        Environment.SetEnvironmentVariable(ChatApiKeyVariable, string.Empty);

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
        Environment.SetEnvironmentVariable(ChatProviderVariable, _previousChatProvider);
        Environment.SetEnvironmentVariable(ChatApiKeyVariable, _previousChatApiKeyVariable);
        Environment.SetEnvironmentVariable(GatewayEnabledVariable, _previousGatewayEnabled);
        Environment.SetEnvironmentVariable(GatewayTokenVariable, _previousGatewayToken);
        Environment.SetEnvironmentVariable(GatewayIdentityNameVariable, _previousGatewayIdentityName);

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
