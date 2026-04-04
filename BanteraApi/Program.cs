using System.Text;
using BanteraApi.Auth;
using BanteraApi.Database;
using BanteraApi.Profile;
using BanteraApi.Storage;
using BanteraApi.Videos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<VideoService>();

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
                        "Access token has expired. POST /api/auth/refresh with your refresh_token to get a new pair.")
                    : new ApiError(ErrorCodes.Unauthorized,
                        "Missing or invalid Authorization header. Add 'Authorization: Bearer <access_token>'.");

                await context.Response.WriteAsJsonAsync(error);
            }
        };
    });

builder.Services.AddAuthorization();

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
            2. **Call APIs** — add header `Authorization: Bearer <access_token>`
            3. **On `401 token_expired`** — `POST /api/auth/refresh` → receive new token pair (old refresh token is revoked)
            4. **On `401 session_expired`** — refresh token is expired/revoked → redirect to login screen
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
.AllowAnonymous();

app.MapPost("/api/auth/register", async (RegisterRequest req, AuthService auth) =>
{
    var (response, errorCode) = await auth.RegisterAsync(req.Email, req.Password);
    if (response is null)
        return Results.Json(
            new ApiError(errorCode ?? ErrorCodes.EmailAlreadyRegistered, "That email is already registered."),
            statusCode: 409);

    return Results.Ok(response);
})
.WithName("Register")
.WithMetadata(new SwaggerOperationAttribute(
    "Register with email + password",
    """
    Creates a new Bantera account using an email address and password, then immediately returns
    an access token + refresh token pair.

    **Current behavior:**
    - Email verification / OTP is not required yet
    - Duplicate email/password accounts are rejected
    - Apple identities are not auto-linked by matching email
    """))
.Produces<LoginResponse>(200)
.Produces<ApiError>(409)
.AllowAnonymous();

app.MapPost("/api/auth/apple", async (AppleLoginRequest req, AuthService auth, CancellationToken cancellationToken) =>
{
    var (response, errorCode) = await auth.LoginWithAppleAsync(req, cancellationToken);
    if (response is null)
    {
        var message = errorCode switch
        {
            ErrorCodes.AppleIdentityMismatch => "The Apple credential does not match the signed identity token.",
            ErrorCodes.AppleAudienceMismatch => "Apple sign-in was issued for a different app identifier. Check the iOS bundle identifier and backend Apple audience config.",
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
        httpContext,
        cancellationToken);

    return response is null
        ? Results.Json(
            new ApiError(
                errorCode ?? ErrorCodes.InvalidProfile,
                "Profile updates must include a valid name and/or translation language."),
            statusCode: 400)
        : Results.Ok(response);
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
    Guid userId,
    ProfileService profileService,
    CancellationToken cancellationToken) =>
{
    var avatar = await profileService.GetAvatarAsync(userId, cancellationToken);
    if (avatar is null)
        return Results.NotFound();

    return Results.Stream(avatar.Stream, avatar.ContentType);
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

app.MapGet("/api/videos/{videoId:guid}/file", async (
    Guid videoId,
    System.Security.Claims.ClaimsPrincipal user,
    VideoService videoService,
    CancellationToken cancellationToken) =>
{
    var file = await videoService.GetVideoFileAsync(
        videoId,
        TryGetUserId(user),
        cancellationToken);

    return file is null
        ? Results.NotFound()
        : Results.Stream(file.Stream, file.ContentType, enableRangeProcessing: true);
})
.WithName("GetVideoFile")
.WithMetadata(new SwaggerOperationAttribute(
    "Get uploaded video file",
    "Streams the stored video file if it is public or owned by the caller."))
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

static Guid? TryGetUserId(System.Security.Claims.ClaimsPrincipal user)
{
    var rawUserId =
        user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
        ?? user.FindFirst("sub")?.Value
        ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    return Guid.TryParse(rawUserId, out var userId)
        ? userId
        : null;
}
