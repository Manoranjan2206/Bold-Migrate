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
var logsPath = builder.Configuration["BoldMigrate:LogsPath"] ?? Path.Combine(AppContext.BaseDirectory, "logs");
builder.Logging.AddProvider(new BoldMigrate.Api.Logging.FileLoggerProvider(logsPath));

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
builder.Services.AddSingleton<BoldMigrate.Engine.Preview.IRenderLogStore, BoldMigrate.Engine.Preview.InMemoryRenderLogStore>();
builder.Services.AddMemoryCache();

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
builder.Services.AddScoped<BoldMigrate.Core.Interfaces.IStagedReportRenderer, BoldMigrate.Api.Preview.StagedReportRenderer>();
builder.Services.AddScoped<ValidationEngine>();
builder.Services.AddScoped<RenderValidationEngine>();
builder.Services.AddScoped<DeltaSyncEngine>();
builder.Services.AddScoped<BoldMigrate.Api.Preview.StagedReportSession>();
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

    // EnsureCreated does not alter an existing schema. ValidationResults gained nullable
    // page-count columns and VisualSimilarity became nullable, so a database created before
    // that change is rebuilt here. Validation results are derived data — re-running validation
    // reproduces them — so recreating the table is safe.
    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        // The table is stale if VisualSimilarity is still NOT NULL, or if the page-count
        // columns are missing. SQLite cannot relax a NOT NULL constraint in place, so the
        // table is recreated.
        var needsRebuild = false;
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('ValidationResults') " +
                "WHERE (name = 'VisualSimilarity' AND [notnull] = 1) " +
                "   OR (name = 'VisualSimilarity' AND 0 = (SELECT COUNT(*) FROM pragma_table_info('ValidationResults') WHERE name = 'RenderSummary'));";
            needsRebuild = Convert.ToInt64(await probe.ExecuteScalarAsync() ?? 0L) > 0;
        }

        if (needsRebuild)
        {
            app.Logger.LogWarning(
                "Rebuilding ValidationResults for the updated schema. Previous validation scores are "
                + "discarded; re-run validation to regenerate them.");

            await db.Database.ExecuteSqlRawAsync("DROP TABLE ValidationResults;");
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE ValidationResults (
                    Id TEXT NOT NULL CONSTRAINT PK_ValidationResults PRIMARY KEY,
                    ReportId TEXT NOT NULL,
                    DataAccuracy TEXT NOT NULL,
                    VisualSimilarity TEXT NULL,
                    RowsSource INTEGER NOT NULL,
                    RowsDestination INTEGER NOT NULL,
                    LayoutShifts INTEGER NOT NULL,
                    SourcePageCount INTEGER NULL,
                    DestinationPageCount INTEGER NULL,
                    RenderSummary TEXT NULL,
                    ParameterSetHash TEXT NOT NULL,
                    RunUtc TEXT NOT NULL
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IX_ValidationResults_ReportId ON ValidationResults (ReportId);");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Could not bring the ValidationResults schema up to date.");
    }

    // Earlier builds re-inserted every discovered item each time discovery ran. Keep the first
    // row per (run, type, source path) and drop the repeats with their dependent rows.
    try
    {
        const string dupes =
            "SELECT Id FROM CatalogItems WHERE rowid NOT IN " +
            "(SELECT MIN(rowid) FROM CatalogItems GROUP BY RunId, ItemType, SourcePath)";
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM ValidationResults WHERE ReportId IN ({dupes});");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM ReportRecords WHERE CatalogItemId IN ({dupes});");
        var removed = await db.Database.ExecuteSqlRawAsync($"DELETE FROM CatalogItems WHERE Id IN ({dupes});");
        if (removed > 0)
            app.Logger.LogWarning("Removed {Count} duplicate catalog items left by repeated discovery.", removed);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Could not remove duplicate catalog items.");
    }

    // ReportRecords gained local-edit tracking for the Step 4 designer. The columns are added
    // in place (nullable / defaulted) so existing run data is kept.
    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT name FROM pragma_table_info('ReportRecords');";
            using var reader = await probe.ExecuteReaderAsync();
            while (await reader.ReadAsync()) existing.Add(reader.GetString(0));
        }

        if (!existing.Contains("LocalEditUtc"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ReportRecords ADD COLUMN LocalEditUtc TEXT NULL;");
        }
        if (!existing.Contains("LocalEditPendingUpload"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE ReportRecords ADD COLUMN LocalEditPendingUpload INTEGER NOT NULL DEFAULT 0;");
        }
        if (!existing.Contains("FeatureTags"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ReportRecords ADD COLUMN FeatureTags TEXT NULL;");
        }

        var valExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT name FROM pragma_table_info('ValidationResults');";
            using var reader = await probe.ExecuteReaderAsync();
            while (await reader.ReadAsync()) valExisting.Add(reader.GetString(0));
        }

        if (!valExisting.Contains("RenderStatus"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ValidationResults ADD COLUMN RenderStatus TEXT NULL;");
        }
        if (!valExisting.Contains("RenderDurationMs"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ValidationResults ADD COLUMN RenderDurationMs INTEGER NULL;");
        }
        if (!valExisting.Contains("ScreenshotRelativePath"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ValidationResults ADD COLUMN ScreenshotRelativePath TEXT NULL;");
        }
        if (!valExisting.Contains("ErrorMessage"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE ValidationResults ADD COLUMN ErrorMessage TEXT NULL;");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Could not bring the ReportRecords schema up to date.");
    }
}

// Bold Reports data extensions for the Step 4 local viewer/designer. SQL Server is built in;
// these let previews, and designer data-source changes, run against every other provider.
BoldReports.Web.ReportConfig.DefaultSettings = new BoldReports.Web.ReportSettings().RegisterExtensions(new List<string>
{
    "BoldReports.Data.AWSSecretsManager",
    "BoldReports.Data.Csv",
    "BoldReports.Data.ElasticSearch",
    "BoldReports.Data.Excel",
    "BoldReports.Data.GoogleBigQuery",
    "BoldReports.Data.MongoDB",
    "BoldReports.Data.MySQL",
    "BoldReports.Data.Oracle",
    "BoldReports.Data.PostgreSQL",
    "BoldReports.Data.Snowflake",
    "BoldReports.Data.SSAS",
    "BoldReports.Data.WebData"
});

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
