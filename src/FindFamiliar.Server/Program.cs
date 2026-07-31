using System.Text.Json.Serialization;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

ConfigureSqliteProvider();

var builder = WebApplication.CreateBuilder(args);
var applicationDataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
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
