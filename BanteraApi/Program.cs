using BanteraApi.Database;
using BanteraApi.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Bantera API",
        Version = "v1"
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.Configure<R2Settings>(builder.Configuration.GetSection(R2Settings.Section));
builder.Services.AddSingleton<R2StorageService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Bantera API v1");
    options.RoutePrefix = "swagger";
});

app.MapGet("/", () => "Hello World!")
    .WithName("HelloWorld");

app.MapGet("/version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    return Results.Ok(new { version });
})
.WithName("GetVersion");

// ── Startup checks ────────────────────────────────────────────────────────────
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
var failures = new List<string>();

// 1. Postgres
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        startupLogger.LogInformation("[Startup] Checking Postgres...");
        await db.Database.OpenConnectionAsync();
        startupLogger.LogInformation("[Startup] Postgres OK — server version: {Version}",
            db.Database.GetDbConnection().ServerVersion);
        await db.Database.CloseConnectionAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogError("[Startup] Postgres FAILED: {Message}", ex.Message);
        failures.Add("Postgres");
    }
}

// 2. Cloudflare R2
var r2 = app.Services.GetRequiredService<R2StorageService>();
try
{
    const string testKey = "bantera-startup-check.txt";
    startupLogger.LogInformation("[Startup] Checking R2...");
    await r2.UploadTextAsync(testKey, "ok");
    await r2.DeleteObjectAsync(testKey);
    startupLogger.LogInformation("[Startup] R2 OK");
}
catch (Exception ex)
{
    startupLogger.LogError("[Startup] R2 FAILED: {Message}", ex.Message);
    failures.Add("R2");
}

// 3. Abort if any check failed
if (failures.Count > 0)
{
    startupLogger.LogCritical("╔══════════════════════════════════════════════╗");
    startupLogger.LogCritical("║         STARTUP CHECKS FAILED — ABORTING     ║");
    startupLogger.LogCritical("╠══════════════════════════════════════════════╣");
    foreach (var f in failures)
        startupLogger.LogCritical("║  ✗ {Service,-42}║", f);
    startupLogger.LogCritical("╚══════════════════════════════════════════════╝");
    Environment.Exit(1);
}

startupLogger.LogInformation("[Startup] All checks passed — starting server.");
// ─────────────────────────────────────────────────────────────────────────────

app.Run();
