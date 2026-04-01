using System.Text;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.Section));
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<AuthService>();

builder.Services.Configure<R2Settings>(builder.Configuration.GetSection(R2Settings.Section));
builder.Services.AddSingleton<R2StorageService>();

// ── JWT Auth ──────────────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        };
    });

builder.Services.AddAuthorization();

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo { Title = "Bantera API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Paste your access_token here. Example: eyJhb..."
    });

});

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Bantera API v1");
    options.RoutePrefix = "swagger";
});

app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapGet("/", () => "Hello World!").WithName("HelloWorld");

app.MapGet("/version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    return Results.Ok(new { version });
}).WithName("GetVersion");

app.MapPost("/api/auth/login", async (LoginRequest req, AuthService auth) =>
{
    var result = await auth.LoginAsync(req.Email, req.Password);
    return result is null
        ? Results.Unauthorized()
        : Results.Ok(result);
})
.WithName("Login")
.AllowAnonymous();

// Example of a protected endpoint
app.MapGet("/api/me", (System.Security.Claims.ClaimsPrincipal user) =>
{
    var userId = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
    return Results.Ok(new { userId });
})
.WithName("GetMe")
.RequireAuthorization();

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

        startupLogger.LogInformation("[Startup] Applying pending migrations...");
        await db.Database.MigrateAsync();

        if (app.Environment.IsDevelopment())
            await DataSeeder.SeedAsync(db, startupLogger);
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
