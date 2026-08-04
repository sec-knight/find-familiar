using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// Coverage for the Sprint 07 teardown stabilization. Sprint 06.5 saw runs fail on Windows during
/// cleanup, after every assertion had already passed, because a SQLite file handle had not been
/// released yet. These tests assert that a database that was actually used is fully removed by
/// disposal — deterministically, with no sleeping in the test itself.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class TemporaryDirectoryCleanupTests
{
    [Fact]
    public async Task Disposing_a_used_temporary_database_removes_its_directory()
    {
        string directory;

        var database = new TemporarySqliteDatabase();
        try
        {
            await using var dbContext = await database.CreateContextAsync();

            dbContext.Workers.Add(new Worker
            {
                Id = Guid.NewGuid(),
                WorkerKey = "cleanup-probe",
                DisplayName = "Cleanup probe",
                Capabilities = "Planner",
                RegisteredUtc = DateTime.UtcNow,
                LastHeartbeatUtc = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();

            Assert.Equal(1, await dbContext.Workers.CountAsync());

            directory = Path.GetDirectoryName(ExtractDataSource(database.ConnectionString))!;
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            database.Dispose();
        }

        Assert.False(Directory.Exists(directory), "The temporary database directory should be gone after disposal.");
    }

    [Fact]
    public async Task Disposal_succeeds_even_when_a_context_was_never_disposed_by_the_test()
    {
        // The exact Sprint 06.5 shape: a context (and therefore a pooled connection) still alive
        // when cleanup runs. Cleanup must still complete rather than throwing after a green test.
        string directory;

        var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        await dbContext.Workers.CountAsync();

        directory = Path.GetDirectoryName(ExtractDataSource(database.ConnectionString))!;
        Assert.True(Directory.Exists(directory));

        database.Dispose();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Deleting_a_directory_that_does_not_exist_is_a_no_op()
    {
        var missing = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", $"never-created-{Guid.NewGuid():N}");

        TemporaryDirectoryCleanup.Delete(missing);

        Assert.False(Directory.Exists(missing));
    }

    private static string ExtractDataSource(string connectionString) =>
        connectionString["Data Source=".Length..];
}
