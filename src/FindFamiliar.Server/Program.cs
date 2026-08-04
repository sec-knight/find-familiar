using System.Text.Json.Serialization;
using FindFamiliar.Server.Api.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

ConfigureSqliteProvider();

var builder = WebApplication.CreateBuilder(args);
var configuredDataDirectory = builder.Configuration["Familiar:DataDirectory"];
var applicationDataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
    ? Path.Combine(builder.Environment.ContentRootPath, "App_Data")
    : configuredDataDirectory;
Directory.CreateDirectory(applicationDataDirectory);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(applicationDataDirectory, "DataProtection-Keys")))
    .SetApplicationName("FindFamiliar");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<FamiliarDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("FindFamiliar")));
builder.Services.AddScoped<IContextProjectionService, ContextProjectionService>();
builder.Services.AddScoped<IWorkQueueService, WorkQueueService>();
builder.Services.AddScoped<ISessionResultCaptureService, SessionResultCaptureService>();
builder.Services.AddScoped<ISessionCancellationService, SessionCancellationService>();
builder.Services.AddScoped<IWorkerCoordinationService, WorkerCoordinationService>();
builder.Services.AddScoped<IWorkerOverviewService, WorkerOverviewService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<RunnerBridgeOptions>(builder.Configuration.GetSection(RunnerBridgeOptions.SectionName));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/tasks/{taskId:guid}/context.md", async (
    Guid taskId,
    IContextProjectionService contextProjection,
    CancellationToken cancellationToken) =>
{
    var document = await contextProjection.GetTaskContextAsync(taskId, cancellationToken);
    return document is null
        ? Results.NotFound()
        : Results.Text(MarkdownContextRenderer.Render(document), "text/markdown; charset=utf-8");
});

app.MapGet("/tasks/{taskId:guid}/context.json", async (
    Guid taskId,
    IContextProjectionService contextProjection,
    CancellationToken cancellationToken) =>
{
    var document = await contextProjection.GetTaskContextAsync(taskId, cancellationToken);
    return document is null ? Results.NotFound() : Results.Ok(document);
});

app.MapGet("/tasks/{taskId:guid}/sessions/{sessionId:guid}/assignment.md", async (
    Guid taskId,
    Guid sessionId,
    IContextProjectionService contextProjection,
    CancellationToken cancellationToken) =>
{
    var document = await contextProjection.GetTaskContextAsync(taskId, cancellationToken);
    if (document is null)
    {
        return Results.NotFound();
    }

    var session = document.Sessions.SingleOrDefault(candidate => candidate.Id == sessionId);
    if (session is null)
    {
        return Results.NotFound();
    }

    if (session.Status != AgentSessionStatus.Started)
    {
        return Results.Conflict(new
        {
            message = "This session is no longer Started. An assignment packet can only be generated for a Started session."
        });
    }

    var markdown = SessionAssignmentMarkdownRenderer.RenderAssignment(document, session);
    return Results.Text(markdown, "text/markdown; charset=utf-8");
});

app.MapRunnerEndpoints();

app.Run();

static void ConfigureSqliteProvider()
{
    if (OperatingSystem.IsWindows())
    {
        raw.SetProvider(new SQLite3Provider_winsqlite3());
    }
    else
    {
        raw.SetProvider(new SQLite3Provider_sqlite3());
    }

    raw.FreezeProvider();
}

public partial class Program { }
