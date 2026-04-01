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

// ── Postgres connectivity test (runs once at startup) ────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var dbLogger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
    try
    {
        dbLogger.LogInformation("[DB Test] Connecting to Postgres...");
        await db.Database.OpenConnectionAsync();
        dbLogger.LogInformation("[DB Test] Connection successful. Server version: {Version}",
            db.Database.GetDbConnection().ServerVersion);
        await db.Database.CloseConnectionAsync();
    }
    catch (Exception ex)
    {
        dbLogger.LogError(ex, "[DB Test] Failed — is the SSH tunnel running?");
    }
}
// ─────────────────────────────────────────────────────────────────────────────

// ── R2 connectivity test (runs once at startup) ───────────────────────────────
var r2 = app.Services.GetRequiredService<R2StorageService>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

try
{
    const string testKey = "bantera-test.txt";
    const string testContent = "Hello from Bantera R2 test!";

    logger.LogInformation("[R2 Test] Uploading test object '{Key}'...", testKey);
    await r2.UploadTextAsync(testKey, testContent);

    logger.LogInformation("[R2 Test] Downloading test object '{Key}'...", testKey);
    var downloaded = await r2.DownloadTextAsync(testKey);
    logger.LogInformation("[R2 Test] Content: {Content}", downloaded);

    logger.LogInformation("[R2 Test] Listing bucket objects...");
    var objects = await r2.ListObjectsAsync();
    logger.LogInformation("[R2 Test] Objects in bucket: {Objects}", string.Join(", ", objects));

    logger.LogInformation("[R2 Test] Deleting test object...");
    await r2.DeleteObjectAsync(testKey);

    logger.LogInformation("[R2 Test] All checks passed.");
}
catch (Exception ex)
{
    logger.LogError(ex, "[R2 Test] Failed — check your R2 credentials in appsettings.Development.json");
}
// ─────────────────────────────────────────────────────────────────────────────

app.Run();
