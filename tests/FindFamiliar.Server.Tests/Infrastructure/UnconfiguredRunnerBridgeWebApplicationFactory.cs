using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// A fully self-contained host with its own isolated temp SQLite database and an explicitly
/// unset "RunnerBridge:Token", used only to exercise the runner bridge's "not configured"
/// behavior. Overrides configuration in-memory (added after every other source, so it always
/// wins) instead of through process environment variables, so it never depends on and never
/// races with <see cref="FindFamiliarWebApplicationFactory"/>'s own environment-variable use —
/// both can safely run concurrently.
/// </summary>
public sealed class UnconfiguredRunnerBridgeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDirectory;

    public UnconfiguredRunnerBridgeWebApplicationFactory()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Familiar:DataDirectory"] = _tempDirectory,
                ["ConnectionStrings:FindFamiliar"] =
                    $"Data Source={Path.Combine(_tempDirectory, "find-familiar-unconfigured-test.db")}",
                ["RunnerBridge:Token"] = null
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        TemporaryDirectoryCleanup.Delete(_tempDirectory);
    }
}
