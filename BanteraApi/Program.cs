using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Data;
using BanteraApi;
using BanteraApi.Account;
using BanteraApi.Admin;
using BanteraApi.Auth;
using BanteraApi.Cloudflare;
using BanteraApi.Database;
using BanteraApi.Database.Entities;
using BanteraApi.Gemini;
using BanteraApi.Profile;
using BanteraApi.RevAi;
using BanteraApi.Storage;
using BanteraApi.Videos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.Annotations;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.Section));
builder.Services.AddSingleton<JwtService>();
builder.Services.Configure<AppleSignInSettings>(builder.Configuration.GetSection(AppleSignInSettings.Section));
builder.Services.AddHttpClient<AppleIdentityTokenValidator>();
builder.Services.AddScoped<AuthService>();

builder.Services.Configure<R2Settings>(builder.Configuration.GetSection(R2Settings.Section));
builder.Services.AddSingleton<R2StorageService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<VideoService>();
builder.Services.AddScoped<AccountDeletionService>();

builder.Services.Configure<CloudflareSettings>(builder.Configuration.GetSection("Cloudflare"));
builder.Services.AddHttpClient("cloudflare", c =>
{
    c.BaseAddress = new Uri("https://api.cloudflare.com");
    c.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddScoped<CloudflareImageService>();

builder.Services.Configure<GeminiSettings>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddHttpClient("gemini", c =>
{
    c.BaseAddress = new Uri("https://generativelanguage.googleapis.com");
    c.Timeout = TimeSpan.FromSeconds(180);
});
builder.Services.AddScoped<GeminiService>();

builder.Services.Configure<RevAiSettings>(builder.Configuration.GetSection(RevAiSettings.Section));
builder.Services.AddHttpClient("revai", c =>
{
    c.BaseAddress = new Uri("https://api.rev.ai");
    c.Timeout = TimeSpan.FromSeconds(180);
});
builder.Services.AddScoped<RevAiAlignmentService>();

builder.Services.AddScoped<AdminService>();

// ── Rate limiting ─────────────────────────────────────────────────────────────
// Reads real client IP from CF-Connecting-IP (Cloudflare), X-Forwarded-For, or RemoteIpAddress.
static string GetClientIp(HttpContext ctx) =>
    ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
    ?? ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
    ?? ctx.Connection.RemoteIpAddress?.ToString()
    ?? "unknown";

builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: GetClientIp(ctx),
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
        }));
    opts.RejectionStatusCode = 429;
});

// ── Forwarded headers (reverse proxy / Cloudflare) ────────────────────────────
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownIPNetworks.Clear();
    opts.KnownProxies.Clear();
});

// ── JWT Auth ──────────────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            // Remove default 5-min clock skew so 15-min tokens expire exactly on time
            ClockSkew = TimeSpan.Zero,
        };

        // Return structured JSON on 401 so the app can check error.code
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";

                var isExpired = context.AuthenticateFailure is
                    Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException;

                var error = isExpired
                    ? new ApiError(ErrorCodes.TokenExpired,
                        "Your session has expired. Please try again.")
                    : new ApiError(ErrorCodes.Unauthorized,
                        "Missing or invalid access token.");

                await context.Response.WriteAsJsonAsync(error);
            }
        };
    });

builder.Services.AddAuthorization(opts =>
    opts.AddPolicy("Admin", policy => policy.RequireRole("admin")));

// ── Swagger ───────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Bantera API",
        Version = "v1",
        Description = """
            ## Authentication flow
            1. **Login** — `POST /api/auth/login` → receive `access_token` (15 min) + `refresh_token` (90 days)
            2. **(Development only)** **Register** — `POST /api/auth/register` → same token response as login (not deployed in non-Development environments)
            3. **Call APIs** — add header `Authorization: Bearer <access_token>`
            4. **On `401 token_expired`** — `POST /api/auth/refresh` → receive new token pair (old refresh token is revoked)
            5. **On `401 session_expired`** — refresh token is expired/revoked → redirect to login screen
            """
    });

    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Paste your access_token (without 'Bearer ' prefix). Obtained from POST /api/auth/login."
    });

    options.EnableAnnotations();
});

var app = builder.Build();
const string GenericApiFailureMessage = "Something went wrong. Please try again.";

// ── Middleware ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Bantera API v1");
    options.RoutePrefix = "swagger";
});
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exceptionFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = exceptionFeature?.Error;
        if (exception is not null && exception is not OperationCanceledException)
        {
            app.Logger.LogError(
                exception,
                "Unhandled API exception for {Method} {Path}",
                context.Request.Method,
                context.Request.Path);
        }

        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new ApiError(ErrorCodes.InternalError, GenericApiFailureMessage));
            return;
        }

        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(GenericApiFailureMessage);
    });
});

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────────

app.MapGet("/", () => "Hello World!")
    .WithName("HelloWorld")
    .WithMetadata(new SwaggerOperationAttribute("Health check", "Returns a plain-text greeting to verify the server is running."));

app.MapGet("/version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    return Results.Ok(new { version });
})
.WithName("GetVersion")
.WithMetadata(new SwaggerOperationAttribute("API version", "Returns the current API version number."))
.Produces<object>(200);

app.MapGet("/api/public/learning-languages", () => Results.Ok(LearningLanguageCatalog.Items))
    .WithName("GetLearningLanguages")
    .WithMetadata(new SwaggerOperationAttribute(
        "Learning languages catalog",
        "Returns BCP-47 identifiers with display names and flag emoji for profile and transcription UI. No authentication required."))
    .Produces<IReadOnlyList<LearningLanguageItem>>(200)
    .AllowAnonymous();

app.MapGet("/api/public/translation-languages", () => Results.Ok(TranslationLanguageCatalog.Items))
    .WithName("GetTranslationLanguages")
    .WithMetadata(new SwaggerOperationAttribute(
        "Translation languages catalog",
        "Returns BCP-47 identifiers for iOS built-in translation supported languages. No authentication required."))
    .Produces<IReadOnlyList<LearningLanguageItem>>(200)
    .AllowAnonymous();

// ── Auth endpoints ─────────────────────────────────────────────────────────────

app.MapPost("/api/auth/login", async (LoginRequest req, AuthService auth) =>
{
    var result = await auth.LoginAsync(req.Email, req.Password);
    return result is null
        ? Results.Json(new ApiError(ErrorCodes.InvalidCredentials, "Email or password is incorrect."), statusCode: 401)
        : Results.Ok(result);
})
.WithName("Login")
.WithMetadata(new SwaggerOperationAttribute(
    "Login with email + password",
    """
    Authenticates a user and returns an access token (15 min) and a refresh token (90 days, rolling).

    **Store the tokens securely:**
    - `access_token` → in-memory only
    - `refresh_token` → iOS Keychain / Android Keystore
    """))
.Produces<LoginResponse>(200)
.Produces<ApiError>(401)
.AllowAnonymous()
.RequireRateLimiting("login");

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/auth/register", async (RegisterRequest req, AuthService auth) =>
    {
        var (response, errorCode) = await auth.RegisterAsync(req.Email, req.Password);
        if (response is null)
        {
            return Results.Json(
                new ApiError(
                    errorCode,
                    "An account with this email already exists."),
                statusCode: 409);
        }

        return Results.Ok(response);
    })
    .WithName("Register")
    .WithMetadata(new SwaggerOperationAttribute(
        "Register with email + password (development only)",
        """
        **Local development only.** This endpoint is not available when `ASPNETCORE_ENVIRONMENT` is not `Development`.

        Creates a new email/password account and returns the same token pair as login.
        """))
    .Produces<LoginResponse>(200)
    .Produces<ApiError>(400)
    .Produces<ApiError>(409)
    .AllowAnonymous();
}

app.MapPost("/api/auth/apple", async (AppleLoginRequest req, AuthService auth, CancellationToken cancellationToken) =>
{
    var (response, errorCode) = await auth.LoginWithAppleAsync(req, cancellationToken);
    if (response is null)
    {
        var message = errorCode switch
        {
            ErrorCodes.AppleIdentityMismatch => "The Apple credential did not match this sign-in attempt.",
            ErrorCodes.AppleAudienceMismatch => "Apple sign-in could not be verified. Please try again.",
            _ => "Apple sign-in could not be verified. Please try again."
        };

        return Results.Json(
            new ApiError(errorCode, message),
            statusCode: 401);
    }

    return Results.Ok(response);
})
.WithName("ContinueWithApple")
.WithMetadata(new SwaggerOperationAttribute(
    "Continue with Apple",
    """
    Verifies the Apple identity token from the native iOS sign-in flow, then either signs the user in
    or creates a new Apple-backed account.

    **Account linking rule:**
    - Matching email alone does **not** link an existing email/password account
    - Apple creates or uses a separate `provider = apple` identity keyed by Apple's subject claim
    """))
.Produces<LoginResponse>(200)
.Produces<ApiError>(401)
.AllowAnonymous();

app.MapPost("/api/auth/refresh", async (RefreshRequest req, AuthService auth) =>
{
    var (response, errorCode) = await auth.RefreshAsync(req.RefreshToken);
    if (response is null)
        return Results.Json(
            new ApiError(errorCode, "Refresh token is expired or has already been used. Please log in again."),
            statusCode: 401);

    return Results.Ok(response);
})
.WithName("RefreshToken")
.WithMetadata(new SwaggerOperationAttribute(
    "Refresh token pair",
    """
    Exchanges a valid refresh token for a **new** access token + refresh token pair.
    The old refresh token is immediately **revoked** (rotation).

    **Refresh token rotation rules:**
    - Each refresh issues a brand-new refresh token
    - The previous refresh token is invalidated — never reuse it
    - Store the latest refresh token returned by every call to this endpoint

    **Error codes:**
    - `session_expired` → refresh token is expired or already used → redirect to login
    """))
.Produces<LoginResponse>(200)
.Produces<ApiError>(401)
.AllowAnonymous();

// ── Protected endpoints ────────────────────────────────────────────────────────

app.MapGet("/api/me", (System.Security.Claims.ClaimsPrincipal user) =>
{
    var userId =
        user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    return Results.Ok(new { userId });
})
.WithName("GetMe")
.WithMetadata(new SwaggerOperationAttribute(
    "Get current user",
    """
    Returns the authenticated user's ID from the JWT.
    Requires `Authorization: Bearer <access_token>` header.

    **Error codes on 401:**
    - `token_expired` → access token expired → call POST /api/auth/refresh
    - `unauthorized` → missing or malformed Authorization header
    """))
.Produces<object>(200)
.Produces<ApiError>(401)
.RequireAuthorization();

app.MapDelete("/api/me", async (
    System.Security.Claims.ClaimsPrincipal user,
    AccountDeletionService accountDeletionService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var deleted = await accountDeletionService.DeleteAccountAsync(userId.Value, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteMyAccount")
.WithMetadata(new SwaggerOperationAttribute(
    "Delete current account",
    """
    Permanently deletes the authenticated user, sessions, identities, saved-video links,
    and non-AI uploads. AI-generated audio is preserved and reassigned to Bantera AI.
    Requires `Authorization: Bearer <access_token>`.

    **Responses:**
    - `204` — account deleted
    - `401` — missing/invalid token
    - `404` — user record not found
    """))
.Produces(StatusCodes.Status204NoContent)
.Produces<ApiError>(401)
.Produces(StatusCodes.Status404NotFound)
.RequireAuthorization();

app.MapGet("/api/me/profile", async (
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    ProfileService profileService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var profile = await profileService.GetProfileAsync(userId.Value, httpContext, cancellationToken);
    return profile is null
        ? Results.NotFound()
        : Results.Ok(profile);
})
.WithName("GetMyProfile")
.WithMetadata(new SwaggerOperationAttribute(
    "Get current profile",
    "Returns the current user's editable profile fields, including name and profile image URL."))
.Produces<UserProfileResponse>(200)
.Produces<ApiError>(401)
.RequireAuthorization();

app.MapPut("/api/me/profile", async (
    UpdateProfileRequest req,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    ProfileService profileService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var (response, errorCode) = await profileService.UpdateProfileAsync(
        userId.Value,
        req.Name,
        req.TranslationLanguage,
        req.NativeLanguage,
        req.LearningLanguage,
        httpContext,
        cancellationToken);

    if (response is null)
    {
        var code = errorCode ?? ErrorCodes.InvalidProfile;
        if (code == ErrorCodes.Unauthorized)
        {
            return Results.Json(
                new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
                statusCode: 401);
        }

        return Results.Json(
            new ApiError(
                code,
                "Profile update failed: include at least one valid field, or fix invalid values."),
            statusCode: 400);
    }

    return Results.Ok(response);
})
.WithName("UpdateMyProfile")
.WithMetadata(new SwaggerOperationAttribute(
    "Update current profile",
    "Updates the current user's editable profile fields such as name and translation language."))
.Produces<UserProfileResponse>(200)
.Produces<ApiError>(400)
.Produces<ApiError>(401)
.RequireAuthorization();

app.MapPost("/api/me/profile-image", async (
    IFormFile file,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    ProfileService profileService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var (response, errorCode) = await profileService.UpdateAvatarAsync(
        userId.Value,
        file,
        httpContext,
        cancellationToken);

    return response is null
        ? Results.Json(
            new ApiError(errorCode ?? ErrorCodes.InvalidProfileImage,
                "Profile image must be a JPEG, PNG, WEBP, HEIC, or HEIF file under 5 MB."),
            statusCode: 400)
        : Results.Ok(response);
})
.DisableAntiforgery()
.WithName("UpdateMyProfileImage")
.WithMetadata(new SwaggerOperationAttribute(
    "Upload current profile image",
    "Uploads a new profile image to R2 for the current user and returns the updated profile."))
.Accepts<IFormFile>("multipart/form-data")
.Produces<UserProfileResponse>(200)
.Produces<ApiError>(400)
.Produces<ApiError>(401)
.RequireAuthorization();

app.MapGet("/api/users/{userId:guid}/avatar", async (
    HttpContext httpContext,
    Guid userId,
    ProfileService profileService,
    CancellationToken cancellationToken) =>
{
    var avatar = await profileService.GetAvatarAsync(userId, cancellationToken);
    if (avatar is null)
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // Avoid hammering Postgres on repeated avatar loads (e.g. list rebuilds).
    httpContext.Response.Headers.CacheControl = "public, max-age=3600";
    httpContext.Response.ContentType = avatar.ContentType;
    await using (avatar.Stream)
    {
        await avatar.Stream.CopyToAsync(httpContext.Response.Body, cancellationToken);
    }
})
.WithName("GetUserAvatar")
.WithMetadata(new SwaggerOperationAttribute(
    "Get user avatar",
    "Returns the stored profile image for a user."))
.Produces(200)
.Produces(404)
.AllowAnonymous();

app.MapPost("/api/me/videos", async (
    [FromForm] UploadVideoRequest req,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var (response, errorCode) = await videoService.UploadVideoAsync(
        userId.Value,
        req,
        httpContext,
        cancellationToken);

    return response is null
        ? Results.Json(
            new ApiError(
                errorCode ?? ErrorCodes.InvalidVideoUpload,
                "Upload a supported MP4, MOV, or M4V file under 250 MB with transcript text, a language code, and valid media metadata."),
            statusCode: 400)
        : Results.Ok(response);
})
.DisableAntiforgery()
.WithName("UploadMyVideo")
.WithMetadata(new SwaggerOperationAttribute(
    "Upload a transcribed video",
    """
    Uploads a prepared video file plus its transcript text for the current user.

    **Video upload rules:**
    - Supported content types: MP4, MOV, M4V
    - Transcript text and transcript language are required
    - Visibility defaults to private in the app, but public videos can be viewed by anyone with the video URL
    """))
.Accepts<UploadVideoRequest>("multipart/form-data")
.Produces<VideoUploadResponse>(200)
.Produces<ApiError>(400)
.Produces<ApiError>(401)
.RequireAuthorization();

app.MapGet("/api/me/videos", async (
    [FromQuery] bool? includeV2,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var videos = await videoService.ListMyVideosAsync(
        userId.Value,
        httpContext,
        includeV2 == true,
        cancellationToken);

    return Results.Ok(videos);
})
.WithName("ListMyVideos")
.WithMetadata(new SwaggerOperationAttribute(
    "List my uploaded videos",
    "Returns the authenticated user's uploaded videos ordered from newest to oldest."))
.Produces<IReadOnlyList<VideoUploadResponse>>(200)
.Produces<ApiError>(401)
.RequireAuthorization();

// Single streaming endpoint — emits SSE events as each step completes.
// Events: {"step":"dialogue"} → {"step":"done","video":{...}}
// Pre-stream errors (401, 429) are returned as plain JSON with the matching status code.
app.MapPost("/api/me/audio/generate", async (
    GenerateAudioRequest req,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    GeminiService geminiService,
    VideoService videoService,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    const string generationFailedMessage = "Something went wrong while creating the practice audio. Please try again.";

    var userId = TryGetUserId(user);
    if (userId is null)
    {
        httpContext.Response.StatusCode = 401;
        await httpContext.Response.WriteAsJsonAsync(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), cancellationToken);
        return;
    }

    const int defaultDailyLimit = 5;
    var todayUtc = DateTime.UtcNow.Date;
    var todayCount = await db.UserVideos
        .CountAsync(v => v.UserId == userId.Value && v.IsAiGenerated && v.CreatedAt >= todayUtc, cancellationToken);
    var customLimit = await db.Users
        .Where(u => u.Id == userId.Value)
        .Select(u => u.AiAudioDailyLimit)
        .FirstOrDefaultAsync(cancellationToken);
    var dailyLimit = customLimit ?? defaultDailyLimit;
    if (todayCount >= dailyLimit)
    {
        httpContext.Response.StatusCode = 429;
        await httpContext.Response.WriteAsJsonAsync(
            new ApiError(ErrorCodes.DailyLimitReached,
                $"You've reached your daily limit of {dailyLimit} AI audio generation{(dailyLimit == 1 ? "" : "s")}. Try again tomorrow."),
            cancellationToken);
        return;
    }

    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers["Cache-Control"] = "no-cache";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";

    var sseOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    async Task SendAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload, sseOpts);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    try
    {
        var dialogue = await geminiService.GenerateDialogueAsync(
            req.Language,
            req.LanguageCode,
            req.Scenario,
            req.DurationSeconds,
            req.ScenarioId,
            req.NativeLanguage,
            req.NativeLanguageCode,
            cancellationToken);
        await SendAsync(new { step = "dialogue", lines = dialogue.Lines.Select(l => l.Text).ToArray() });
        var flattenedShortCueTexts = BuildFlattenedShortCueTexts(dialogue.Lines);

        var (wavBytes, durationMs) = await geminiService.GenerateAudioAsync(dialogue, req.LanguageCode, cancellationToken);
        var cues = geminiService.EstimateCues(dialogue.Lines, durationMs);
        var videoResponse = await videoService.SaveAiAudioAsync(userId.Value, dialogue.Title, wavBytes, req.Language, req.LanguageCode, cues, durationMs, httpContext, cancellationToken);
        await SendAsync(new { step = "done", video = videoResponse });
    }
    catch (ContentRejectedException ex)
    {
        await SendAsync(new { step = "error", message = ex.Message });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(
            ex,
            "Practice audio generation failed for user {UserId}, locale {LanguageCode}, scenarioId {ScenarioId}",
            userId,
            req.LanguageCode,
            req.ScenarioId);
        await SendAsync(new { step = "error", message = generationFailedMessage });
    }
})
.WithName("GenerateAiAudio")
.RequireAuthorization();

// V2 keeps the same SSE shape while adding server-side alignment metadata.
// Events: {"step":"dialogue"} → {"step":"audio"} → {"step":"aligning"} → {"step":"done","video":{...}}
app.MapPost("/api/me/audio/generate/v2", async (
    GenerateAudioRequest req,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    GeminiService geminiService,
    RevAiAlignmentService revAiAlignmentService,
    IOptions<RevAiSettings> revAiOptions,
    R2StorageService r2StorageService,
    VideoService videoService,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    const string generationFailedMessage = "Something went wrong while creating the practice audio. Please try again.";

    var userId = TryGetUserId(user);
    if (userId is null)
    {
        httpContext.Response.StatusCode = 401;
        await httpContext.Response.WriteAsJsonAsync(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), cancellationToken);
        return;
    }

    const int defaultDailyLimit = 5;
    var todayUtc = DateTime.UtcNow.Date;
    var todayCount = await db.UserVideos
        .CountAsync(v => v.UserId == userId.Value && v.IsAiGenerated && v.CreatedAt >= todayUtc, cancellationToken);
    var customLimit = await db.Users
        .Where(u => u.Id == userId.Value)
        .Select(u => u.AiAudioDailyLimit)
        .FirstOrDefaultAsync(cancellationToken);
    var dailyLimit = customLimit ?? defaultDailyLimit;
    if (todayCount >= dailyLimit)
    {
        httpContext.Response.StatusCode = 429;
        await httpContext.Response.WriteAsJsonAsync(
            new ApiError(ErrorCodes.DailyLimitReached,
                $"You've reached your daily limit of {dailyLimit} AI audio generation{(dailyLimit == 1 ? "" : "s")}. Try again tomorrow."),
            cancellationToken);
        return;
    }

    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers["Cache-Control"] = "no-cache";
    httpContext.Response.Headers["X-Accel-Buffering"] = "no";

    var sseOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    async Task SendAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload, sseOpts);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    try
    {
        var dialogue = await geminiService.GenerateDialogueAsync(
            req.Language,
            req.LanguageCode,
            req.Scenario,
            req.DurationSeconds,
            req.ScenarioId,
            req.NativeLanguage,
            req.NativeLanguageCode,
            cancellationToken);
        await SendAsync(new { step = "dialogue", lines = dialogue.Lines.Select(l => l.Text).ToArray() });
        var flattenedShortCueTexts = BuildFlattenedShortCueTexts(dialogue.Lines);

        var (wavBytes, durationMs) = await geminiService.GenerateAudioAsync(dialogue, req.LanguageCode, cancellationToken);
        var objectKey = $"videos/{userId.Value}/{Guid.NewGuid():N}.wav";
        await r2StorageService.UploadObjectAsync(
            objectKey,
            new MemoryStream(wavBytes),
            "audio/wav",
            cancellationToken);
        await SendAsync(new { step = "audio" });
        await SendAsync(new { step = "aligning" });

        IReadOnlyList<WordTimingRecord>? wordTiming = null;
        IReadOnlyList<VideoTranscriptCueRecord>? cues = null;
        IReadOnlyList<VideoTranscriptCueRecord>? shortCues = null;
        var revAiRequired = RevAiAlignmentService.TryGetSupportedLanguageCode(
            req.LanguageCode,
            out var revAiLanguageCode);
        if (revAiRequired && revAiLanguageCode is not null)
        {
            var transcript = string.Join("\n", dialogue.Lines.Select(l => l.Text));
            var revAiLogOptions = revAiOptions.Value;
            var diagnostics = RevAiTranscriptDiagnostics.Create(
                transcript,
                revAiLogOptions.TranscriptPreviewMaxChars);
            var presignedUrl = r2StorageService.GeneratePresignedUrl(objectKey, TimeSpan.FromHours(1));
            app.Logger.LogInformation(
                "Rev.ai alignment attempt for user {UserId}, locale {LanguageCode}, revAiLanguageCode {RevAiLanguageCode}, scenarioId {ScenarioId}, audioUrl {AudioUrl}, transcriptChars {TranscriptChars}, transcriptLines {TranscriptLines}, transcriptHash {TranscriptHash}, normalizedTranscriptHash {NormalizedTranscriptHash}, transcriptPreview {TranscriptPreview}, transcriptFull {TranscriptFull}",
                userId,
                req.LanguageCode,
                revAiLanguageCode,
                req.ScenarioId,
                presignedUrl,
                diagnostics.CharCount,
                diagnostics.LineCount,
                diagnostics.TranscriptHash,
                diagnostics.NormalizedTranscriptHash,
                revAiLogOptions.LogTranscriptPreview ? diagnostics.TranscriptPreview : "(disabled)",
                revAiLogOptions.LogTranscriptFull ? transcript : "(disabled)");
            try
            {
                wordTiming = await revAiAlignmentService.AlignAsync(
                    presignedUrl,
                    revAiLanguageCode,
                    transcript,
                    cancellationToken);
                if (!RevAiCueAlignmentBuilder.TryBuild(
                        dialogue.Lines,
                        wordTiming,
                        out cues,
                        out var failure))
                {
                    app.Logger.LogWarning(
                        "Rev.ai cue alignment failed for user {UserId}, locale {LanguageCode}, scenarioId {ScenarioId}. lineIndex={LineIndex}, matchedWords={MatchedWords}, expectedWords={ExpectedWords}, matchRatio={MatchRatio}, requiredMatchRatio={RequiredMatchRatio}, requiredMatchedWords={RequiredMatchedWords}, expectedToken={ExpectedToken}, actualWord={ActualWord}",
                        userId,
                        req.LanguageCode,
                        req.ScenarioId,
                        failure?.LineIndex,
                        failure?.MatchedWords,
                        failure?.ExpectedWords,
                        failure?.MatchRatio,
                        failure?.RequiredMatchRatio,
                        failure?.RequiredMatchedWords,
                        failure?.ExpectedToken,
                        failure?.ActualWord);
                    throw new InvalidOperationException(
                        $"Rev.ai cue alignment produced non-alignable output for supported locale '{req.LanguageCode}' at line {failure?.LineIndex}.");
                }

                if (flattenedShortCueTexts.Count > 0)
                {
                    var shortCueLines = flattenedShortCueTexts
                        .Select(text => new DialogueLine("Speaker1", text))
                        .ToArray();
                    if (!RevAiCueAlignmentBuilder.TryBuild(
                            shortCueLines,
                            wordTiming,
                            out shortCues,
                            out var shortCueFailure))
                    {
                        app.Logger.LogWarning(
                            "Rev.ai short-cue alignment failed for user {UserId}, locale {LanguageCode}, scenarioId {ScenarioId}. lineIndex={LineIndex}, matchedWords={MatchedWords}, expectedWords={ExpectedWords}, matchRatio={MatchRatio}, requiredMatchRatio={RequiredMatchRatio}, requiredMatchedWords={RequiredMatchedWords}, expectedToken={ExpectedToken}, actualWord={ActualWord}",
                            userId,
                            req.LanguageCode,
                            req.ScenarioId,
                            shortCueFailure?.LineIndex,
                            shortCueFailure?.MatchedWords,
                            shortCueFailure?.ExpectedWords,
                            shortCueFailure?.MatchRatio,
                            shortCueFailure?.RequiredMatchRatio,
                            shortCueFailure?.RequiredMatchedWords,
                            shortCueFailure?.ExpectedToken,
                            shortCueFailure?.ActualWord);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                app.Logger.LogError(
                    ex,
                    "Rev.ai alignment failed for supported locale. user {UserId}, locale {LanguageCode}, revAiLanguageCode {RevAiLanguageCode}, scenarioId {ScenarioId}, audioUrl {AudioUrl}",
                    userId,
                    req.LanguageCode,
                    revAiLanguageCode,
                    req.ScenarioId,
                    presignedUrl);
                await SendAsync(new { step = "error", message = generationFailedMessage });
                return;
            }
        }
        else
        {
            cues = await geminiService.GenerateCueTimingAsync(
                wavBytes,
                "audio/wav",
                dialogue.Lines,
                durationMs,
                cancellationToken);
        }

        if (cues is null)
        {
            await SendAsync(new { step = "error", message = generationFailedMessage });
            return;
        }

        var videoResponse = await videoService.SaveAiAudioV2Async(
            userId.Value,
            dialogue.Title,
            objectKey,
            wavBytes.LongLength,
            req.Language,
            req.LanguageCode,
            dialogue.Lines,
            wordTiming,
            cues,
            shortCues,
            durationMs,
            httpContext,
            cancellationToken);
        await SendAsync(new { step = "done", video = videoResponse });
    }
    catch (ContentRejectedException ex)
    {
        await SendAsync(new { step = "error", message = ex.Message });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogError(
            ex,
            "Practice audio generation v2 failed for user {UserId}, locale {LanguageCode}, scenarioId {ScenarioId}",
            userId,
            req.LanguageCode,
            req.ScenarioId);
        await SendAsync(new { step = "error", message = generationFailedMessage });
    }
})
.WithName("GenerateAiAudioV2")
.RequireAuthorization();

// Corrects phone-transcribed cues using the original dialogue as ground truth.
// Returns corrected cues with identical timestamps.
app.MapPost("/api/me/videos/{videoId:guid}/transcript/correct", async (
    Guid videoId,
    CorrectTranscriptRequest req,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    GeminiService geminiService,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

    var transcribedCues = req.TranscribedCues
        .Select(c => new VideoTranscriptCueRecord(c.Index, c.StartMs, c.EndMs, c.Text))
        .ToList();

    var corrected = await geminiService.CorrectTranscriptAsync(req.OriginalLines, transcribedCues, cancellationToken);

    var transcriptText = string.Join("\n", corrected.Select(c => c.Text));
    var videoTranscriptCues = corrected.Select(c => new VideoTranscriptCue(c.Index, c.StartMs, c.EndMs, c.Text)).ToList();

    var response = await videoService.UpdateTranscriptAsync(videoId, userId.Value, transcriptText, videoTranscriptCues, httpContext, cancellationToken);
    return response is null ? Results.NotFound() : Results.Ok(response);
})
.WithName("CorrectVideoTranscript")
.Produces<VideoUploadResponse>(200)
.Produces<ApiError>(401)
.Produces(404)
.RequireAuthorization();

app.MapPatch("/api/me/videos/{videoId:guid}/transcript", async (
    Guid videoId,
    UpdateTranscriptRequest req,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var response = await videoService.UpdateTranscriptAsync(
        videoId, userId.Value, req.TranscriptText, req.TranscriptCues, httpContext, cancellationToken);

    return response is null
        ? Results.NotFound()
        : Results.Ok(response);
})
.WithName("UpdateVideoTranscript")
.Produces<VideoUploadResponse>(200)
.Produces<ApiError>(401)
.Produces(404)
.RequireAuthorization();

app.MapDelete("/api/me/videos/{videoId:guid}", async (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."), statusCode: 401);

    var deleted = await videoService.DeleteVideoAsync(videoId, userId.Value, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
})
.WithName("DeleteMyVideo")
.Produces(204)
.Produces<ApiError>(401)
.Produces(404)
.RequireAuthorization();

app.MapPost("/api/me/videos/{videoId:guid}/remove-from-list", async (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null)
        return Results.Json(
            new ApiError(ErrorCodes.Unauthorized, "Missing or invalid access token."),
            statusCode: 401);

    var removed = await videoService.RemoveAiAudioFromOwnerListAsync(
        videoId,
        userId.Value,
        cancellationToken);

    return removed ? Results.NoContent() : Results.NotFound();
})
.WithName("RemoveMyAiAudioFromList")
.Produces(204)
.Produces<ApiError>(401)
.Produces(404)
.RequireAuthorization();

app.MapGet("/api/videos/public", async (
    [FromQuery] string? languageCode,
    [FromQuery] int limit,
    [FromQuery] int offset,
    [FromQuery] string? search,
    [FromQuery] string? mediaType,
    [FromQuery] bool? includeV2,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var safeLimit = Math.Clamp(limit <= 0 ? 20 : limit, 1, 50);
    var safeOffset = Math.Max(offset, 0);
    var excludeUserId = TryGetUserId(user);
    var videos = await videoService.ListPublicVideosAsync(
        languageCode,
        excludeUserId,
        safeLimit,
        safeOffset,
        search,
        mediaType,
        httpContext,
        includeV2 != false,
        cancellationToken);

    return Results.Ok(videos);
})
.WithName("ListPublicVideos")
.WithMetadata(new SwaggerOperationAttribute(
    "List public videos",
    """
    Returns public videos, newest first, optionally filtered by transcript language code
    and full-text searched across file name and transcript. Supports offset-based pagination.
    When authenticated, videos owned by the caller are excluded.
    """))
.Produces<IReadOnlyList<VideoUploadResponse>>(200)
.AllowAnonymous();

// ── Saved videos ──────────────────────────────────────────────────────────────

app.MapPost("/api/me/saved/{videoId:guid}", async (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();
    var ok = await videoService.SaveVideoAsync(userId.Value, videoId, cancellationToken);
    return ok ? Results.NoContent() : Results.NotFound();
})
.WithName("SaveVideo")
.RequireAuthorization();

app.MapDelete("/api/me/saved/{videoId:guid}", async (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();
    await videoService.UnsaveVideoAsync(userId.Value, videoId, cancellationToken);
    return Results.NoContent();
})
.WithName("UnsaveVideo")
.RequireAuthorization();

app.MapGet("/api/me/saved/{videoId:guid}", async (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();
    var isSaved = await videoService.IsVideoSavedAsync(userId.Value, videoId, cancellationToken);
    return Results.Ok(new { isSaved });
})
.WithName("CheckVideoSaved")
.RequireAuthorization();

app.MapGet("/api/me/saved", async (
    [FromQuery] bool? includeV2,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();
    var videos = await videoService.ListSavedVideosAsync(userId.Value, httpContext, includeV2 == true, cancellationToken);
    return Results.Ok(videos);
})
.WithName("ListSavedVideos")
.RequireAuthorization();

// ── Saved cues ─────────────────────────────────────────────────────────────
app.MapPost("/api/me/saved-cues", async (
    SaveCueRequest req,
    System.Security.Claims.ClaimsPrincipal user,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();

    var video = await db.UserVideos.FindAsync([req.VideoId], cancellationToken);
    if (video is null || (!video.IsPublic && video.UserId != userId.Value))
        return Results.NotFound();

    var existing = await db.UserSavedCues.FirstOrDefaultAsync(
        c => c.UserId == userId.Value && c.VideoId == req.VideoId && c.CueId == req.CueId,
        cancellationToken);
    var metadata = ResolveSavedCueMetadata(req, video);
    if (existing is not null)
    {
        await TryUpdateSavedCueMetadataAsync(db, existing.Id, metadata, cancellationToken);
        return Results.Ok(new { id = existing.Id });
    }

    var entry = new UserSavedCue
    {
        UserId = userId.Value,
        VideoId = req.VideoId,
        CueId = req.CueId,
        CueIndex = req.CueIndex,
        SavedAt = DateTime.UtcNow,
    };
    db.UserSavedCues.Add(entry);
    await db.SaveChangesAsync(cancellationToken);
    await TryUpdateSavedCueMetadataAsync(db, entry.Id, metadata, cancellationToken);
    return Results.Created($"/api/me/saved-cues/{entry.Id}", new { id = entry.Id });
})
.WithName("SaveCue")
.RequireAuthorization();

app.MapDelete("/api/me/saved-cues/{entryId:guid}", async (
    Guid entryId,
    System.Security.Claims.ClaimsPrincipal user,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();

    var entry = await db.UserSavedCues.FirstOrDefaultAsync(
        c => c.Id == entryId && c.UserId == userId.Value, cancellationToken);
    if (entry is null) return Results.NotFound();

    db.UserSavedCues.Remove(entry);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("UnsaveCue")
.RequireAuthorization();

app.MapGet("/api/me/saved-cues", async (
    [FromQuery] bool? includeV2,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();
    var cues = await videoService.ListSavedCuesAsync(userId.Value, httpContext, includeV2 == true, cancellationToken);
    return Results.Ok(cues);
})
.WithName("ListSavedCues")
.RequireAuthorization();

app.MapGet("/api/me/stats", async (
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();
    var uploadCount = await videoService.GetUploadCountAsync(userId.Value, cancellationToken);
    var savedCount = await videoService.GetSavedCountAsync(userId.Value, cancellationToken);
    return Results.Ok(new { uploadCount, savedCount });
})
.WithName("GetMyStats")
.RequireAuthorization();

app.MapGet("/api/videos/{videoId:guid}", async (
    Guid videoId,
    HttpContext httpContext,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var response = await videoService.GetVideoAsync(
        videoId,
        TryGetUserId(user),
        httpContext,
        cancellationToken);

    return response is null
        ? Results.NotFound()
        : Results.Ok(response);
})
.WithName("GetVideoMetadata")
.WithMetadata(new SwaggerOperationAttribute(
    "Get video metadata",
    "Returns transcript and playback metadata for a video if it is public or owned by the caller."))
.Produces<VideoUploadResponse>(200)
.Produces(404)
.AllowAnonymous();

app.MapGet("/api/videos/{videoId:guid}/file", async Task<IResult> (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    HttpContext httpContext,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var file = await videoService.GetVideoFileAsync(
        videoId,
        TryGetUserId(user),
        cancellationToken);

    if (file is null)
        return Results.NotFound();

    httpContext.Response.ContentLength = file.ContentLength;
    return Results.Stream(file.Stream, file.ContentType, enableRangeProcessing: true);
})
.WithName("GetVideoFile")
.WithMetadata(new SwaggerOperationAttribute(
    "Get uploaded video file",
    "Streams the stored video file if it is public or owned by the caller."))
.Produces(200)
.Produces(404)
.AllowAnonymous();

app.MapGet("/api/videos/{videoId:guid}/cover", async (
    Guid videoId,
    R2StorageService r2StorageService,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var video = await db.UserVideos.FindAsync([videoId], cancellationToken);
    if (video is null || video.CoverImageObjectKey is null)
        return Results.NotFound();

    var file = await r2StorageService.DownloadObjectAsync(video.CoverImageObjectKey, cancellationToken);
    return file is null
        ? Results.NotFound()
        : Results.Stream(file.Stream, video.CoverImageObjectKey!.EndsWith(".jpg") ? "image/jpeg" : "image/png");
})
.WithName("GetVideoCoverImage")
.Produces(200)
.Produces(404)
.AllowAnonymous();

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
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE user_videos
            ADD COLUMN IF NOT EXISTS "TranscriptShortCuesJson" jsonb;
            """);
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

AdminEndpoints.Map(app);

app.Run();

static Guid? TryGetUserId(System.Security.Claims.ClaimsPrincipal user)
{
    var rawUserId =
        user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    if (!Guid.TryParse(rawUserId, out var userId) || userId == Guid.Empty)
        return null;

    return userId;
}

static IReadOnlyList<string> BuildFlattenedShortCueTexts(DialogueLine[] lines)
{
    if (lines.Length == 0)
        return [];

    if (!lines.Any(line => line.ShortCues.Count > 1))
        return [];

    var flattened = new List<string>();
    foreach (var line in lines)
        flattened.AddRange(line.ShortCues);
    return flattened;
}

static SavedCueMetadata ResolveSavedCueMetadata(SaveCueRequest req, UserVideo video)
{
    var cueText = NormalizeSavedCueText(req.CueText);
    var startTimeMs = NormalizeCueTime(req.StartTimeMs);
    var endTimeMs = NormalizeCueTime(req.EndTimeMs);
    var cueMode = NormalizeCueMode(req.CueMode);
    var parentCueId = NormalizeSavedCueId(req.ParentCueId);
    var parentCueIndex = NormalizeCueIndex(req.ParentCueIndex);

    var storedLongCues = ParseSavedCueTranscriptCues(video.TranscriptCuesJson);
    var storedShortCues = ParseSavedCueTranscriptCues(video.TranscriptShortCuesJson);
    var shouldUseShortCue =
        cueMode == "short"
        || IsShortCueId(req.CueId);
    var primaryCues = shouldUseShortCue ? storedShortCues : storedLongCues;

    if (cueText is null || startTimeMs is null || endTimeMs is null)
    {
        var currentCue = FindCurrentSavedCue(
            primaryCues,
            video.Id,
            req.CueId,
            req.CueIndex);
        if (currentCue is null)
        {
            currentCue = FindCurrentSavedCue(
                shouldUseShortCue ? storedLongCues : storedShortCues,
                video.Id,
                req.CueId,
                req.CueIndex);
        }
        if (currentCue is not null)
        {
            cueText ??= NormalizeSavedCueText(currentCue.Text);
            startTimeMs ??= currentCue.StartMs;
            endTimeMs ??= currentCue.EndMs;
        }
    }

    var parentCue = storedLongCues.FirstOrDefault(c => c.Index == parentCueIndex);
    if (parentCue is null && startTimeMs is not null)
    {
        parentCue = storedLongCues.FirstOrDefault(c =>
            startTimeMs.Value >= c.StartMs && startTimeMs.Value < c.EndMs);
    }
    if (parentCue is null)
    {
        parentCue = storedLongCues.FirstOrDefault(c =>
            c.Index == req.CueIndex || req.CueId == $"{video.Id}-{c.Index}");
    }

    if (parentCue is not null)
    {
        parentCueIndex ??= parentCue.Index;
        parentCueId ??= $"{video.Id}-{parentCue.Index}";
    }

    if (cueMode is null)
    {
        cueMode = shouldUseShortCue && storedShortCues.Count > 0
            ? "short"
            : "long";
    }

    return new SavedCueMetadata(
        cueText,
        startTimeMs,
        endTimeMs,
        cueMode,
        parentCueId,
        parentCueIndex);
}

static VideoTranscriptCue? FindCurrentSavedCue(
    IReadOnlyList<VideoTranscriptCue> cues,
    Guid videoId,
    string cueId,
    int cueIndex)
{
    if (cues.Count == 0)
        return null;

    return cues.FirstOrDefault(c =>
        c.Index == cueIndex
        || cueId == $"{videoId}-{c.Index}"
        || cueId == $"{videoId}-s{c.Index}");
}

static bool IsShortCueId(string? cueId)
{
    if (string.IsNullOrWhiteSpace(cueId))
        return false;

    var marker = cueId.LastIndexOf("-s", StringComparison.OrdinalIgnoreCase);
    if (marker <= 0)
        return false;

    return int.TryParse(cueId[(marker + 2)..], out _);
}

static IReadOnlyList<VideoTranscriptCue> ParseSavedCueTranscriptCues(string? transcriptCuesJson)
{
    if (string.IsNullOrWhiteSpace(transcriptCuesJson))
        return [];
    try
    {
        return JsonSerializer.Deserialize<List<VideoTranscriptCue>>(
                transcriptCuesJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? [];
    }
    catch
    {
        return [];
    }
}

static string? NormalizeSavedCueText(string? value)
{
    var normalized = value?.Trim();
    if (string.IsNullOrEmpty(normalized))
        return null;
    return normalized.Length <= 4000 ? normalized : normalized[..4000];
}

static string? NormalizeSavedCueId(string? value)
{
    var normalized = value?.Trim();
    if (string.IsNullOrEmpty(normalized))
        return null;
    return normalized.Length <= 255 ? normalized : normalized[..255];
}

static string? NormalizeCueMode(string? value)
{
    var normalized = value?.Trim().ToLowerInvariant();
    return normalized is "short" or "long" ? normalized : null;
}

static int? NormalizeCueTime(int? value)
{
    return value is >= 0 ? value : null;
}

static int? NormalizeCueIndex(int? value)
{
    return value is >= 0 ? value : null;
}

static async Task TryUpdateSavedCueMetadataAsync(
    AppDbContext db,
    Guid entryId,
    SavedCueMetadata metadata,
    CancellationToken cancellationToken)
{
    if (!metadata.HasAnyValue)
        return;
    if (!await SavedCueMetadataColumnsExistAsync(db, cancellationToken))
        return;

    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != ConnectionState.Open;
    if (shouldClose)
        await connection.OpenAsync(cancellationToken);

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE user_saved_cues
            SET
                "CueText" = COALESCE(@cueText, "CueText"),
                "StartTimeMs" = COALESCE(@startTimeMs, "StartTimeMs"),
                "EndTimeMs" = COALESCE(@endTimeMs, "EndTimeMs"),
                "CueMode" = COALESCE(@cueMode, "CueMode"),
                "ParentCueId" = COALESCE(@parentCueId, "ParentCueId"),
                "ParentCueIndex" = COALESCE(@parentCueIndex, "ParentCueIndex")
            WHERE "Id" = @id
            """;
        AddDbParameter(command, "id", entryId);
        AddDbParameter(command, "cueText", metadata.CueText);
        AddDbParameter(command, "startTimeMs", metadata.StartTimeMs);
        AddDbParameter(command, "endTimeMs", metadata.EndTimeMs);
        AddDbParameter(command, "cueMode", metadata.CueMode);
        AddDbParameter(command, "parentCueId", metadata.ParentCueId);
        AddDbParameter(command, "parentCueIndex", metadata.ParentCueIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    finally
    {
        if (shouldClose)
            await connection.CloseAsync();
    }
}

static async Task<bool> SavedCueMetadataColumnsExistAsync(
    AppDbContext db,
    CancellationToken cancellationToken)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != ConnectionState.Open;
    if (shouldClose)
        await connection.OpenAsync(cancellationToken);

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_name = 'user_saved_cues'
              AND column_name IN (
                  'CueText',
                  'StartTimeMs',
                  'EndTimeMs',
                  'CueMode',
                  'ParentCueId',
                  'ParentCueIndex'
              )
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) == 6;
    }
    finally
    {
        if (shouldClose)
            await connection.CloseAsync();
    }
}

static void AddDbParameter(IDbCommand command, string name, object? value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value ?? DBNull.Value;
    command.Parameters.Add(parameter);
}

sealed record SavedCueMetadata(
    string? CueText,
    int? StartTimeMs,
    int? EndTimeMs,
    string? CueMode,
    string? ParentCueId,
    int? ParentCueIndex)
{
    public bool HasAnyValue =>
        CueText is not null ||
        StartTimeMs is not null ||
        EndTimeMs is not null ||
        CueMode is not null ||
        ParentCueId is not null ||
        ParentCueIndex is not null;
}

