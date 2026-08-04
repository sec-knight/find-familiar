using FindFamiliar.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// A standalone, file-backed, migrated SQLite database for tests that exercise
/// FamiliarDbContext-dependent services directly, without the HTTP pipeline.
/// </summary>
public sealed class TemporarySqliteDatabase : IDisposable
{
    private readonly string _directory;
    private readonly List<FamiliarDbContext> _contexts = [];

    public string ConnectionString { get; }

    public TemporarySqliteDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        ConnectionString = $"Data Source={Path.Combine(_directory, "find-familiar-unit-test.db")}";
    }

    public async Task<FamiliarDbContext> CreateContextAsync()
    {
        var options = new DbContextOptionsBuilder<FamiliarDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        var dbContext = new FamiliarDbContext(options);
        await dbContext.Database.MigrateAsync();
        _contexts.Add(dbContext);
        return dbContext;
    }

    public void Dispose()
    {
        // Dispose every context this instance handed out, whether or not the test also disposed
        // it. Disposal is idempotent, and doing it here means the directory delete below never
        // races an undisposed connection.
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _contexts.Clear();

        TemporaryDirectoryCleanup.Delete(_directory);
    }
}
