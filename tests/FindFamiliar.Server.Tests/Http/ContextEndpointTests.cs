using System.Net;
using System.Text.Json;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class ContextEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Markdown_and_json_endpoints_expose_the_same_canonical_identities()
    {
        Guid projectId, taskId, sessionId, entryId;

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

            var project = new FamiliarProject
            {
                Id = Guid.NewGuid(),
                Name = $"Parity project {Guid.NewGuid():N}",
                Purpose = "Seeded for ContextEndpointTests.",
                Status = ProjectStatus.Active,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            var task = new FamiliarTask
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Title = "Parity task",
                RequestedOutcome = "Prove endpoint parity.",
                Status = TaskStatus.Ready,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            var session = new AgentSession
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                Role = AgentSessionRole.Planner,
                Status = AgentSessionStatus.Started,
                ContextRevisionRead = 0,
                StartedUtc = DateTime.UtcNow
            };
            var entry = new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                TaskId = task.Id,
                SourceSessionId = session.Id,
                Kind = ContextEntryKind.Plan,
                Title = "Parity entry",
                Content = "Distinctive parity content.",
                State = ContextEntryState.Active,
                CreatedUtc = DateTime.UtcNow
            };

            dbContext.AddRange(project, task, session, entry);
            await dbContext.SaveChangesAsync();

            projectId = project.Id;
            taskId = task.Id;
            sessionId = session.Id;
            entryId = entry.Id;
        }

        using var client = factory.CreateClient();

        var markdownResponse = await client.GetAsync($"/tasks/{taskId}/context.md");
        Assert.Equal(HttpStatusCode.OK, markdownResponse.StatusCode);
        var markdown = await markdownResponse.Content.ReadAsStringAsync();

        var jsonResponse = await client.GetAsync($"/tasks/{taskId}/context.json");
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        var json = await jsonResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(projectId, document.RootElement.GetProperty("project").GetProperty("id").GetGuid());
        Assert.Equal(taskId, document.RootElement.GetProperty("task").GetProperty("id").GetGuid());
        Assert.Contains(sessionId.ToString(), markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(entryId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Distinctive parity content.", markdown);
    }
}
