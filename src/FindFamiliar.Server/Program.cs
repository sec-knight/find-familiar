using System.Text.Json.Serialization;
using FindFamiliar.Server.Api.Familiar;
using FindFamiliar.Server.Api.Runner;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Services.Familiar.Chat.Brief;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
using FindFamiliar.Server.Services.Familiar.Chat.Retrieval;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using FindFamiliar.Server.Services.Providers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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

// ---- The talk lane (ADR-0013), independent of the Runner and of the per-project conversation ----
//
// A send commits a Pending turn and returns; the hosted service below generates it out of band, so a
// reply survives the connection that asked for it going away. The queue is a singleton because it is
// the handoff between the two, and it holds a scheduling hint only — the durable record is the row.
builder.Services.AddSingleton<FamiliarChatGenerationQueue>();
builder.Services.AddScoped<IFamiliarChatService, FamiliarChatService>();

// The system-wide projection the Familiar answers from, and this server's own record of what it has
// sent. Both are read-only and neither needs a credential.
builder.Services.AddScoped<IFamiliarStandingBriefService, FamiliarStandingBriefService>();
builder.Services.AddScoped<IFamiliarContextRetrievalService, FamiliarContextRetrievalService>();
builder.Services.AddScoped<IFamiliarChatUsageService, FamiliarChatUsageService>();
builder.Services.AddHostedService<FamiliarChatGenerationHost>();

builder.Services.Configure<FamiliarChatOptions>(
    builder.Configuration.GetSection(FamiliarChatOptions.SectionName));

// Which conversational provider answers, chosen by configuration and nothing else.
//
// The default is the honest one, exactly as it is for reasoning (ADR-0012) and for provider capacity
// (ADR-0011): with nothing configured the application starts, /Familiar renders, a conversation is
// durable, generation runs end to end, and the one sentence that is true is what appears. No
// credential is required to run this application at all.
//
// Both the provider name *and* a present key are required. A configured provider with no key would
// otherwise produce a stream that dies on every turn — a dead stream where an honest sentence
// belongs.
var chatOptions = builder.Configuration.GetSection(FamiliarChatOptions.SectionName).Get<FamiliarChatOptions>();

if (chatOptions?.IsConfigured() == true)
{
    // A named client so the timeout, base address and credential are configured once, at startup,
    // rather than per request. The key is read from the environment here and never from
    // configuration, so it cannot be committed or printed by a configuration dump.
    builder.Services.AddHttpClient<IFamiliarChatProvider, OpenAiCompatibleFamiliarChatProvider>(
        (services, client) =>
        {
            var settings = services.GetRequiredService<IOptions<FamiliarChatOptions>>().Value;

            // A trailing slash matters: without it the last path segment is replaced rather than
            // appended, and "/v1" silently becomes "/chat/completions".
            var baseAddress = settings.BaseAddress.EndsWith('/')
                ? settings.BaseAddress
                : settings.BaseAddress + "/";

            client.BaseAddress = new Uri(baseAddress);

            // Infinite, deliberately: HttpClient's own timeout would abort a *streamed* response that
            // is arriving perfectly well but taking its time. The bound that matters is the linked
            // token inside the provider, which distinguishes our timeout from the caller's
            // cancellation. A timeout here could not.
            client.Timeout = Timeout.InfiniteTimeSpan;

            if (settings.ReadApiKey() is { } apiKey)
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
        });

    // Drafts a plan from a turn that asked for one, through the same provider. Writes a proposal and
    // nothing else — no task, no session, no context entry (ADR-0014). Registered here rather than
    // unconditionally because it needs a provider, and with none configured nothing can draft.
    builder.Services.AddScoped<IFamiliarPlanDraftingService, FamiliarPlanDraftingService>();

    builder.Services.AddScoped<IFamiliarChatGenerator, ProviderFamiliarChatGenerator>();
}
else
{
    builder.Services.AddScoped<IFamiliarChatGenerator, UnconfiguredFamiliarChatGenerator>();
}

// The only bridge from a proposal to persisted work. Effects go through IWorkflowDispatchService,
// so work confirmed from a conversation is indistinguishable from work created by hand.
builder.Services.AddScoped<IFamiliarActionService, FamiliarActionService>();

// The same bridge for a drafted plan: every effect goes through IWorkflowDispatchService with gates
// re-checked inside the committing transaction, so work approved in conversation is
// indistinguishable from work created by hand. Registered unconditionally — deciding a plan that
// already exists must keep working even if the provider that drafted it is later unconfigured.
builder.Services.AddScoped<IFamiliarPlanApprovalService, FamiliarPlanApprovalService>();

// What is waiting on a human, across every project the Familiar may be told about. Read-only:
// deciding a handoff goes through ISessionHandoffApprovalService, the same transaction the task
// pages use.
builder.Services.AddScoped<IFamiliarOpenDecisionsService, FamiliarOpenDecisionsService>();

builder.Services.Configure<FamiliarReasoningOptions>(
    builder.Configuration.GetSection(FamiliarReasoningOptions.SectionName));

builder.Services.Configure<OpenAiCompatibleReasoningOptions>(
    builder.Configuration.GetSection(OpenAiCompatibleReasoningOptions.SectionName));

// Which reasoning provider answers, chosen by configuration and nothing else.
//
// The default is the honest one, exactly as UnknownProviderCapacityReader is for provider capacity
// (ADR-0011): with nothing configured the application starts, the Familiar page renders, the
// deterministic summary is complete, and a sent message is durably saved and answered with the one
// sentence that is true. No credential is required to run this application at all.
//
// Selecting a real provider is a configuration change, never a code change — that is the whole point
// of the abstraction, and it is what lets one build serve a model on the operator's own machine and
// a hosted endpoint alike.
var reasoningProvider = builder.Configuration["Familiar:Reasoning:Provider"];

if (string.Equals(reasoningProvider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase))
{
    // A named client so the timeout, base address and credential are configured once, at startup,
    // rather than per request. The key is read from the environment here and never from
    // configuration, so it cannot be committed or printed by a configuration dump.
    builder.Services.AddHttpClient<IFamiliarReasoningProvider, OpenAiCompatibleFamiliarReasoningProvider>(
        (services, client) =>
        {
            var settings = services.GetRequiredService<IOptions<OpenAiCompatibleReasoningOptions>>().Value;

            // A trailing slash matters: without it the last path segment is replaced rather than
            // appended, and "/v1" silently becomes "/chat/completions".
            var baseAddress = settings.BaseAddress.EndsWith('/')
                ? settings.BaseAddress
                : settings.BaseAddress + "/";

            client.BaseAddress = new Uri(baseAddress);
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);

            if (settings.ReadApiKey() is { } apiKey)
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
        });
}
else
{
    builder.Services.AddScoped<IFamiliarReasoningProvider, UnconfiguredFamiliarReasoningProvider>();
}

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

app.MapFamiliarChatEndpoints();
app.MapFamiliarChatStreamEndpoint();

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
