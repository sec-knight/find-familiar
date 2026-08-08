using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The trusted machine-local route for reporting durable context, over the wire.
///
/// The rules are asserted in <c>ProjectContextRecordingServiceTests</c>, against the service that owns
/// them. What is left here is what only a real request can prove: that the credential is actually in
/// front of it, that it is <b>not</b> part of the Summoning Gate, and that a typed outcome becomes the
/// status code a caller can act on.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProjectContextEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    private static string Route(Guid projectId) => $"/api/context/projects/{projectId}/entries";

    // ---------------------------------------------------------------- the credential

    [Fact]
    public async Task An_unauthenticated_call_is_refused()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The gateway credential is not this credential. An external client reaching the Summoning Gate
    /// must not be able to write context: that would be exactly the generic write capability this slice
    /// was told not to create.
    /// </summary>
    [Fact]
    public async Task The_gateway_token_cannot_reach_this_route()
    {
        var project = await SeedProjectAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var response = await client.PostAsJsonAsync(Route(project.Id), Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync(project.Id));
    }

    // ---------------------------------------------------------------- recording

    [Fact]
    public async Task A_trusted_caller_records_context_and_is_told_the_new_revision()
    {
        var project = await SeedProjectAsync();
        using var client = Authenticated();

        using var response = await client.PostAsJsonAsync(Route(project.Id), Body("Decision", "RepositoryVerified"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = document.GetProperty("contextEntryId").GetGuid();

        Assert.True(document.GetProperty("contextRevision").GetInt32() > 0);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entry = await dbContext.ContextEntries.SingleAsync(candidate => candidate.Id == entryId);

        Assert.Equal(ContextProvenance.RepositoryVerified, entry.Provenance);
        Assert.Equal("integration-test", entry.RecordedBy);
    }

    /// <summary>
    /// Categories and provenance travel as names. A caller writes what it means, and renumbering an
    /// enum cannot silently change what a stored record says.
    /// </summary>
    [Theory]
    [InlineData("decision", "repositoryverified")]
    [InlineData("SUMMARY", "HUMANREPORTED")]
    [InlineData("Implementation", "SessionReported")]
    public async Task Category_and_provenance_names_are_accepted_case_insensitively(string kind, string provenance)
    {
        var project = await SeedProjectAsync();
        using var client = Authenticated();

        using var response = await client.PostAsJsonAsync(Route(project.Id), Body(kind, provenance));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("NotACategory", "RepositoryVerified")]
    [InlineData("Decision", "NotAProvenance")]
    [InlineData("Decision", "Unspecified")]
    public async Task An_unusable_category_or_provenance_is_refused(string kind, string provenance)
    {
        var project = await SeedProjectAsync();
        using var client = Authenticated();

        using var response = await client.PostAsJsonAsync(Route(project.Id), Body(kind, provenance));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync(project.Id));
    }

    [Fact]
    public async Task An_unknown_project_answers_not_found_and_writes_nothing()
    {
        using var client = Authenticated();
        var totalBefore = await TotalEntryCountAsync();

        using var response = await client.PostAsJsonAsync(Route(Guid.NewGuid()), Body());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(totalBefore, await TotalEntryCountAsync());
    }

    [Fact]
    public async Task A_stale_expected_revision_answers_conflict()
    {
        var project = await SeedProjectAsync();
        using var client = Authenticated();

        using var response = await client.PostAsJsonAsync(
            Route(project.Id),
            new
            {
                kind = "Decision",
                title = "Stale",
                content = "Body.",
                provenance = "RepositoryVerified",
                expectedContextRevision = -1
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync(project.Id));
    }

    /// <summary>
    /// The route records context and offers nothing else. There is no verb here that creates work, and
    /// this is the assertion that notices if one appears.
    /// </summary>
    [Fact]
    public async Task The_route_creates_no_tasks_sessions_or_proposals()
    {
        var project = await SeedProjectAsync();
        using var client = Authenticated();
        var before = await WorkCountsAsync();

        await client.PostAsJsonAsync(Route(project.Id), new
        {
            kind = "Plan",
            title = "Create a task and start a session",
            content = "Approve the plan and dispatch an implementer.",
            provenance = "ExternalReported"
        });

        Assert.Equal(before, await WorkCountsAsync());
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Authenticated()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);

        return client;
    }

    private static object Body(string kind = "Decision", string provenance = "RepositoryVerified") => new
    {
        kind,
        title = $"Endpoint probe {Guid.NewGuid():N}",
        content = "A body recorded through the trusted machine-local route.",
        provenance,
        recordedBy = "integration-test"
    };

    private async Task<FamiliarProject> SeedProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Context endpoint project {Guid.NewGuid():N}",
            Purpose = "Seeded for ProjectContextEndpointTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        return project;
    }

    private async Task<int> EntryCountAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.ContextEntries.CountAsync(entry => entry.ProjectId == projectId);
    }

    private async Task<int> TotalEntryCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.ContextEntries.CountAsync();
    }

    private async Task<(int Tasks, int Sessions, int Proposals)> WorkCountsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.Tasks.CountAsync(),
            await dbContext.AgentSessions.CountAsync(),
            await dbContext.FamiliarPlanProposals.CountAsync());
    }
}
