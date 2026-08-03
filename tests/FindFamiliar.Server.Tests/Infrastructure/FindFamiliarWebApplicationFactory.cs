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

    private readonly string? _previousDataDirectory;
    private readonly string? _previousConnectionString;

    public string TempDirectory { get; }

    public FindFamiliarWebApplicationFactory()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectory);

        _previousDataDirectory = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        _previousConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        Environment.SetEnvironmentVariable(DataDirectoryVariable, TempDirectory);
        Environment.SetEnvironmentVariable(
            ConnectionStringVariable,
            $"Data Source={Path.Combine(TempDirectory, "find-familiar-test.db")}");

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

        if (Directory.Exists(TempDirectory))
        {
            Directory.Delete(TempDirectory, recursive: true);
        }
    }
}
