using FindFamiliar.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
        var dbContext = CreateUnmigratedContext();
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }

    /// <summary>
    /// A context that gives up immediately when the database is locked instead of retrying for the
    /// default thirty seconds.
    ///
    /// Needed to observe SQLITE_BUSY deliberately: with the normal thirty-second timeout a contended
    /// write waits rather than failing, which is correct in production and useless in a test that
    /// must prove how a busy database is classified.
    ///
    /// One second rather than zero — zero means wait forever, not give up immediately.
    /// </summary>
    public async Task<FamiliarDbContext> CreateImpatientContextAsync()
    {
        var options = new DbContextOptionsBuilder<FamiliarDbContext>()
            .UseSqlite($"{ConnectionString};Default Timeout=1;Pooling=False")
            .Options;

        var dbContext = new FamiliarDbContext(options);
        _contexts.Add(dbContext);
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }

    /// <summary>
    /// A context migrated only as far as <paramref name="targetMigration"/>, for tests that need to
    /// seed a database as it existed before a later migration and then apply it.
    /// </summary>
    public async Task<FamiliarDbContext> CreateContextAtMigrationAsync(string targetMigration)
    {
        var dbContext = CreateUnmigratedContext();
        await dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);
        return dbContext;
    }

    private FamiliarDbContext CreateUnmigratedContext()
    {
        var options = new DbContextOptionsBuilder<FamiliarDbContext>()
            .UseSqlite(ConnectionString)
            .Options;

        var dbContext = new FamiliarDbContext(options);
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
