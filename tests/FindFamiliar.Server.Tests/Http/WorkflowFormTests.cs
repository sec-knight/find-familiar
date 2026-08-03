using System.Net;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class WorkflowFormTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task CreateTask_succeeds_without_sibling_project_context_fields()
    {
        var project = await SeedProjectAsync();
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, html) = await afClient.GetPageAsync($"/Projects/Details/{project.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Projects/Details/{project.Id}?handler=CreateTask",
            token,
            [
                new("NewTask.Title", "Add regression coverage for the durable workflow"),
                new("NewTask.RequestedOutcome", "A production-shaped automated suite protects the durable workflow.")
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Tasks/Details/", response.Headers.Location!.ToString());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var task = await dbContext.Tasks.SingleAsync(candidate =>
            candidate.ProjectId == project.Id && candidate.Title == "Add regression coverage for the durable workflow");

        Assert.Equal(TaskStatus.Ready, task.Status);
    }

    [Fact]
    public async Task CreateProjectContext_succeeds_without_sibling_task_fields()
    {
        var project = await SeedProjectAsync();
        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));

        var (_, html) = await afClient.GetPageAsync($"/Projects/Details/{project.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        var response = await afClient.PostFormAsync(
            $"/Projects/Details/{project.Id}?handler=CreateProjectContext",
            token,
            [
                new("NewProjectContext.Kind", nameof(ContextEntryKind.Goal)),
                new("NewProjectContext.Title", "Project-wide goal"),
                new("NewProjectContext.Content", "Protect the durable workflow with automated regression coverage.")
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entry = await dbContext.ContextEntries.SingleAsync(candidate =>
            candidate.ProjectId == project.Id && candidate.Title == "Project-wide goal");

        Assert.Null(entry.TaskId);
        Assert.Equal(ContextEntryState.Active, entry.State);
        Assert.Equal(ContextEntryKind.Goal, entry.Kind);
    }

    [Fact]
    public async Task StartSession_succeeds_without_sibling_context_entry_or_status_fields()
    {
        var project = await SeedProjectAsync();
        var task = await SeedTaskAsync(project.Id);

        var afClient = new AntiforgeryHttpClient(factory.CreateClient(new() { AllowAutoRedirect = false }));
        var (_, html) = await afClient.GetPageAsync($"/Tasks/Details/{task.Id}");
        var token = AntiforgeryHttpClient.ExtractAntiforgeryToken(html);

        int contextRevisionBeforeStart;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
            contextRevisionBeforeStart = await dbContext.Projects
                .Where(candidate => candidate.Id == project.Id)
                .Select(candidate => candidate.ContextRevision)
                .SingleAsync();
        }

        var response = await afClient.PostFormAsync(
            $"/Tasks/Details/{task.Id}?handler=StartSession",
            token,
            [
                new("NewSession.Role", nameof(AgentSessionRole.Planner))
            ]);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("sessionId=", response.Headers.Location!.ToString());

        using var verifyScope = factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var session = await verifyDbContext.AgentSessions.SingleAsync(candidate => candidate.TaskId == task.Id);

        Assert.Equal(AgentSessionRole.Planner, session.Role);
        Assert.Equal(AgentSessionStatus.Started, session.Status);
        Assert.Equal(contextRevisionBeforeStart + 1, session.ContextRevisionRead);

        var refreshedProject = await verifyDbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(contextRevisionBeforeStart + 1, refreshedProject.ContextRevision);
    }

    private async Task<FamiliarProject> SeedProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Test project {Guid.NewGuid():N}",
            Purpose = "Seeded for WorkflowFormTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    private async Task<FamiliarTask> SeedTaskAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = $"Seeded task {Guid.NewGuid():N}",
            RequestedOutcome = "Seeded for WorkflowFormTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }
}
