using System.Net;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class WorkPageTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    /// <summary>
    /// The multiple-Started-sessions alert this test used to cover is no longer reachable through the
    /// application: IX_AgentSessions_TaskId_Started forbids the state. NeedsAttention survives as a
    /// corruption detector for a database restored from before that migration, and its derivation is
    /// proved in WorkQueueServiceTests on an isolated database where the index can safely be dropped.
    /// </summary>
    public async Task Work_queue_lists_task_with_continue_link()
    {
        var uniqueMarker = Guid.NewGuid().ToString("N");
        var (project, task, session) = await SeedTaskWithStartedSessionAsync(uniqueMarker);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/Work");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(task.Title, html);
        Assert.Contains($"sessionId={session.Id}", html);
    }

    [Fact]
    public async Task Navigation_exposes_a_link_to_the_work_queue()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("href=\"/Work\"", html);
        Assert.Contains("Work queue", html);
    }

    private async Task<(FamiliarProject Project, FamiliarTask Task, AgentSession Session)> SeedTaskWithStartedSessionAsync(string marker)
    {
        var project = await SeedProjectAsync($"Work queue project {marker}");
        var task = await SeedTaskAsync(project.Id, $"Work queue task {marker}");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Role = AgentSessionRole.Planner,
            Status = AgentSessionStatus.Started,
            ContextRevisionRead = 0,
            StartedUtc = DateTime.UtcNow
        };
        dbContext.AgentSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return (project, task, session);
    }

    private async Task<FamiliarProject> SeedProjectAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = "Seeded for WorkPageTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    private async Task<FamiliarTask> SeedTaskAsync(Guid projectId, string title)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            RequestedOutcome = "Seeded for WorkPageTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }
}
