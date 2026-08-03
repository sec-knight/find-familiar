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
        return dbContext;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
