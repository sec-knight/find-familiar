using FindFamiliar.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// The automated suite must never read or write the developer's real Familiar database. Sprint 07
/// adds a schema migration and a worker table, so this guarantee is now asserted explicitly rather
/// than relied on: a test run that leaked into App_Data would otherwise register fake workers and
/// claim real sessions.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProductionDatabaseIsolationTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public void Test_host_data_directory_is_a_temporary_directory()
    {
        Assert.True(Directory.Exists(factory.TempDirectory));
        Assert.StartsWith(Path.GetTempPath(), factory.TempDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_host_connection_string_points_into_the_temporary_directory()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var connectionString = dbContext.Database.GetConnectionString();

        Assert.NotNull(connectionString);
        Assert.Contains(factory.TempDirectory, connectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("App_Data", connectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_writes_land_in_the_temporary_database_file()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        // Forces the file to exist and the migration (including the Workers table) to have run.
        await dbContext.Workers.CountAsync();

        var databaseFiles = Directory.GetFiles(factory.TempDirectory, "*.db", SearchOption.AllDirectories);
        Assert.NotEmpty(databaseFiles);
    }

    [Fact]
    public void Repository_App_Data_is_never_used_by_the_suite()
    {
        // Compare the exact database file set and metadata captured before the test host started.
        // This proves the suite made no production write without confusing an intentional live
        // proof performed shortly before the suite with test contamination.
        var after = FindFamiliarWebApplicationFactory.CaptureProductionDatabaseFiles(factory.RepositoryRoot);

        Assert.Equal(factory.ProductionDatabaseFilesBeforeHost.Count, after.Count);
        foreach (var (relativePath, beforeSnapshot) in factory.ProductionDatabaseFilesBeforeHost)
        {
            Assert.True(
                after.TryGetValue(relativePath, out var afterSnapshot),
                $"Production database file '{relativePath}' disappeared during the test run.");
            Assert.Equal(beforeSnapshot, afterSnapshot);
        }
    }
}
