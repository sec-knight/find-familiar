using System.Text.Json.Serialization;
using FindFamiliar.Server.Api.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using FindFamiliar.Server.Services.Providers;
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
builder.Services.AddScoped<IWorkflowDispatchService, WorkflowDispatchService>();
builder.Services.AddScoped<IConversationIntakeService, ConversationIntakeService>();
builder.Services.AddScoped<IConversationDetailsService, ConversationDetailsService>();
builder.Services.AddScoped<IWorkProposalService, WorkProposalService>();
builder.Services.AddScoped<IWorkApprovalService, WorkApprovalService>();
builder.Services.AddScoped<ISessionHandoffService, SessionHandoffService>();
builder.Services.AddScoped<ISessionHandoffApprovalService, SessionHandoffApprovalService>();
builder.Services.AddScoped<IDemiplaneProjectionService, DemiplaneProjectionService>();

// Deterministic project state for the Familiar. No reasoning provider is registered: the snapshot
// and its summary are the part of the Familiar that must work with no credentials at all.
builder.Services.AddScoped<IProjectSnapshotService, ProjectSnapshotService>();

builder.Services.AddScoped<IFamiliarConversationService, FamiliarConversationService>();

// The only bridge from a proposal to persisted work. Effects go through IWorkflowDispatchService,
// so work confirmed from a conversation is indistinguishable from work created by hand.
builder.Services.AddScoped<IFamiliarActionService, FamiliarActionService>();

builder.Services.Configure<FamiliarReasoningOptions>(
    builder.Configuration.GetSection(FamiliarReasoningOptions.SectionName));

// The honest default, exactly as UnknownProviderCapacityReader is for provider capacity (ADR-0011):
// with nothing configured the application starts, the Familiar page renders, the deterministic
// summary is complete, and a message sent on a stock build is durably saved and answered with the
// one sentence that is true. No credential is required to run this application, and none is read
// from configuration — a provider that needs a key reads it from the environment only.
builder.Services.AddScoped<IFamiliarReasoningProvider, UnconfiguredFamiliarReasoningProvider>();

builder.Services.AddScoped<IProviderCapacityService, ProviderCapacityService>();

// The only provider this application invokes is Claude, through the compiled adapter, and it exposes
// no non-interactive usage surface — so the honest reading is Unknown. ADR-0011 records what a real
// reader would require and why estimating instead was rejected.
builder.Services.AddScoped<IProviderCapacityReader>(services => new UnknownProviderCapacityReader(
    "Claude",
    services.GetRequiredService<TimeProvider>(),
    "The Claude Code CLI exposes no non-interactive usage or quota surface, so remaining capacity cannot be read."));
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
