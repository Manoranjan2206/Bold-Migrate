using BoldMigrate.Api.Health;
using BoldMigrate.Api.Middleware;
using BoldMigrate.Core.Configuration;
using BoldMigrate.Core.Interfaces;
using BoldMigrate.Engine.DeltaSync;
using BoldMigrate.Engine.ExpressionTester;
using BoldMigrate.Engine.LocalFolder;
using BoldMigrate.Engine.Logging;
using BoldMigrate.Engine.Orchestrator;
using BoldMigrate.Engine.Transform;
using BoldMigrate.Engine.Upload;
using BoldMigrate.Engine.Validation;
using BoldMigrate.Infrastructure.Credentials;
using BoldMigrate.Infrastructure.Data;
using BoldMigrate.Infrastructure.Staging;
using Microsoft.EntityFrameworkCore;

var contentRoot = AppContext.BaseDirectory;
var webRoot = Path.Combine(contentRoot, "wwwroot");
if (!Directory.Exists(webRoot))
{
    var devPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "BoldMigrate.Api", "wwwroot");
    if (Directory.Exists(devPath))
    {
        contentRoot = Path.Combine(Directory.GetCurrentDirectory(), "src", "BoldMigrate.Api");
        webRoot = devPath;
    }
    else if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")))
    {
        contentRoot = Directory.GetCurrentDirectory();
        webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = webRoot
});

// Configuration
var settings = new MigrationSettings();
builder.Configuration.GetSection("BoldMigrate").Bind(settings);
builder.Services.AddSingleton(settings);

// Database (SQLite)
var dbPath = Path.Combine(AppContext.BaseDirectory, "boldmigrate.db");
builder.Services.AddDbContext<MigrationDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}");
});

// Infrastructure services
builder.Services.AddScoped<IMigrationStore, MigrationStoreRepository>();
builder.Services.AddSingleton<IStagingStore>(new FileSystemStagingStore(settings.StagingRootPath));
builder.Services.AddSingleton<ICredentialStore, InMemoryCredentialStore>();

// Logging & Rules
builder.Services.AddSingleton<CompatibilityRulesLoader>(sp =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "data", "compatibility-rules.json");
    if (!File.Exists(path))
    {
        path = Path.Combine(Directory.GetCurrentDirectory(), "data", "compatibility-rules.json");
    }
    return new CompatibilityRulesLoader(path);
});
builder.Services.AddScoped<MigrationLogger>(sp =>
{
    var store = sp.GetRequiredService<IMigrationStore>();
    var logger = sp.GetRequiredService<ILogger<MigrationLogger>>();
    return new MigrationLogger(logger, store);
});

// Engine services
builder.Services.AddScoped<TransformEngine>();
builder.Services.AddScoped<UploadLayer>();
builder.Services.AddScoped<ValidationEngine>();
builder.Services.AddScoped<DeltaSyncEngine>();
builder.Services.AddSingleton<LocalFolderMigrationService>();
builder.Services.AddSingleton<ExpressionTesterEngine>();

// Orchestrator (Single instance hosted background service)
builder.Services.AddSingleton<MigrationOrchestrator>();
builder.Services.AddSingleton<IMigrationOrchestrator>(sp => sp.GetRequiredService<MigrationOrchestrator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MigrationOrchestrator>());

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<MigrationHealthChecks>("boldmigrate");

// Controllers & CORS
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Auto-migrate / ensure database created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MigrationDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors("AllowAll");

// Serve frontend from wwwroot (pure HTML + JavaScript + CSS design tokens)
app.UseDefaultFiles();
app.UseStaticFiles();

// URL Compatibility Shim middleware (redirects SSRS /ReportServer requests)
app.UseMiddleware<UrlShimMiddleware>();

app.UseRouting();

// Health check endpoint
app.MapHealthChecks("/health");

// API Controllers
app.MapControllers();

// Single service fallback: Any unmapped non-API GET request loads index.html
app.MapFallbackToFile("index.html");

app.Run();
