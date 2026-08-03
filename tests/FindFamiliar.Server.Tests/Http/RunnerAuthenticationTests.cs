using System.Net;
using System.Net.Http.Headers;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Authentication behavior for the whole "/api/runner" route group. Uses the assignment route as
/// a representative endpoint for header/credential matrix cases, since the filter is shared by
/// all three routes.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RunnerAuthenticationTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Unconfigured_bridge_returns_503_and_does_not_look_up_resources()
    {
        await using var unconfiguredFactory = new UnconfiguredRunnerBridgeWebApplicationFactory();
        using var client = unconfiguredFactory.CreateClient();

        var response = await client.GetAsync(
            $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Missing_authorization_header_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Basic", "dXNlcjpwYXNz")]
    [InlineData("Bearer", "")]
    public async Task Malformed_authorization_header_returns_401(string scheme, string parameter)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment");
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, parameter is "" ? null : parameter);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_bearer_token_returns_401()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-configured-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_auth_response_is_independent_of_resource_existence()
    {
        using var client = factory.CreateClient();
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        using var unknownRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/runner/tasks/{Guid.NewGuid()}/sessions/{Guid.NewGuid()}/assignment");
        unknownRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var unknownResponse = await client.SendAsync(unknownRequest);

        using var knownRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");
        knownRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        var knownResponse = await client.SendAsync(knownRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, knownResponse.StatusCode);

        var unknownBytes = await unknownResponse.Content.ReadAsByteArrayAsync();
        var knownBytes = await knownResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(unknownBytes, knownBytes);
    }

    [Fact]
    public async Task Valid_bearer_token_passes_authentication()
    {
        using var client = factory.CreateClient();
        var (_, task, session) = await SeedStartedSessionAsync(AgentSessionRole.Planner);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/runner/tasks/{task.Id}/sessions/{session.Id}/assignment");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FindFamiliarWebApplicationFactory.RunnerBridgeTestToken);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedStartedSessionAsync(AgentSessionRole role)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for RunnerAuthenticationTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for RunnerAuthenticationTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = role,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        };

        dbContext.AddRange(project, task, session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }
}
