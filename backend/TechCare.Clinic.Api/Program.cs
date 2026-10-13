using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.File;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Features.Ai;
using TechCare.Clinic.Api.Features.Alerts;
using TechCare.Clinic.Api.Features.Patients;
using TechCare.Clinic.Api.Features.Queue;
using TechCare.Clinic.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ── Logging (PLAN_ADDENDUM.md 7.7) ────────────────────────
// Structured JSON, one line per event, into a daily-rotating file (the path is configurable via Logging:FilePath
// so the test host points at its own temp folder). No medical content may ever reach the ordinary logs — the AI
// services deliberately log no question text.
var logFile = config["Logging:FilePath"] ?? "logs/app-.log";
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .WriteTo.File(new CompactJsonFormatter(), logFile,
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .CreateLogger();
builder.Host.UseSerilog();

// ── Database ──────────────────────────────────────────────
var provider = config["Database:Provider"] ?? "Sqlite";
builder.Services.AddDbContext<ClinicDbContext>(options =>
{
    if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(config.GetConnectionString("PostgreSql"));
    else
        options.UseSqlite(config.GetConnectionString("Sqlite") ?? "Data Source=clinic.db");
});

// ── Auth ──────────────────────────────────────────────────
var jwtOptions = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwtOptions.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set (at least 32 characters). Use an environment variable in production: Jwt__Key.");

var jwtService = new JwtTokenService(jwtOptions);
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(jwtService);
builder.Services.AddSingleton<UserSessionValidator>();
// PLAN.md 10.3: the cached per-clinic "is this clinic read-only" decision behind the subscription gate.
builder.Services.AddSingleton<SubscriptionGuard>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = jwtService.ValidationParameters();
        options.Events = new JwtBearerEvents
        {
            // SignalR sends the token as a query-string parameter because browsers can't set headers on WebSockets.
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },

            // A signature-valid token is only half of the story (PLAN_ADDENDUM.md 7.1): the account must still
            // exist, be active, and carry the same TokenVersion/Role as the database. A disabled or re-issued
            // account is therefore rejected within the validator's 30 second cache window, no matter how long
            // the token's own lifetime is.
            OnTokenValidated = async ctx =>
            {
                var validator = ctx.HttpContext.RequestServices.GetRequiredService<UserSessionValidator>();
                if (!await validator.IsStillValidAsync(ctx.Principal, ctx.HttpContext.RequestAborted))
                {
                    // The account no longer matches what the token claims — reject the request with 401.
                    // ctx.Fail() throws the SecurityTokenException the JwtBearer handler turns into a
                    // AuthenticateResult.Fail; throwing a raw exception here would surface as a 500 instead.
                    ctx.Fail("Token invalidation requested");
                }
            }
        };
    });
// PLAN.md 1.4: one named policy per permission key; PermissionHandler resolves the account's effective set
// (explicit choices first, role defaults otherwise) against it.
builder.Services.AddAuthorization(o =>
{
    foreach (var key in Permissions.All)
        o.AddPolicy(key, p => p.AddRequirements(new PermissionRequirement(key)));
});
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

// ── CORS (only needed when the React dev server runs on another port) ──
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
// PLAN_ADDENDUM.md 7.6: in production the UI may live on another origin; an empty list would boot the app and
// then fail on every cross-origin call. Refuse to start instead, with a message that points at the fix.
StartupGuards.EnsureCorsConfiguredForProduction(builder.Environment.EnvironmentName, allowedOrigins);

// ── Rate limiting for anonymous endpoints (online booking, tracking) ──
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("booking", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
    // The limit stays 10 in production; the integration test host raises it because every request there comes
    // from one fake IP and the whole suite shares the same 5 minute window.
    var loginPermitLimit = int.TryParse(config["RateLimit:LoginPermitLimit"], out var loginLimit) ? loginLimit : 10;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPermitLimit, Window = TimeSpan.FromMinutes(5) }));
});

// ── App services ──────────────────────────────────────────
// PLAN_ADDENDUM.md 7.6: behind a reverse proxy every request looks like it comes from the proxy's own address,
// and the rate limiter keys on RemoteIpAddress — so production flips ForwardedHeaders:Enabled to trust the proxy.
var trustForwardedHeaders = string.Equals(config["ForwardedHeaders:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

builder.Services.AddSignalR();
builder.Services.AddScoped<QueueService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<QueueNotifier>();
builder.Services.AddScoped<PatientCodeGenerator>();
// PLAN_ADDENDUM.md 9.3: physically removes logically deleted patient files once their clinic's retention
// period has elapsed.
// The singleton is registered under its own type as well so the integration tests can drive a purge
// deterministically; the host runs the very same instance.
builder.Services.AddSingleton<PatientRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PatientRetentionService>());

builder.Services.Configure<AiOptions>(config.GetSection("Ai"));
// PLAN.md 10.2: the installation's default free-trial length (a clinic's own TrialDays overrides it).
builder.Services.Configure<SubscriptionOptions>(config.GetSection("Subscription"));
builder.Services.AddScoped<AiSettingsService>();
builder.Services.Configure<VectorStoreOptions>(config.GetSection("VectorStore"));
builder.Services.AddHttpClient<LmStudioClient>();
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<LibraryVectorCache>();
builder.Services.AddSingleton<PharmacyBrandMap>();
builder.Services.AddScoped<QueryRouter>();
builder.Services.AddScoped<MedicalLibraryService>();
builder.Services.AddScoped<ClinicalAssistantService>();
builder.Services.AddScoped<AlertService>();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// PLAN_ADDENDUM.md 7.7: standard problem+json bodies with Arabic titles, so the UI never shows a raw "404 Not Found".
builder.Services.AddProblemDetails(o =>
{
    o.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Title = ctx.HttpContext.Response.StatusCode switch
        {
            StatusCodes.Status400BadRequest => "طلب غير صالح",
            StatusCodes.Status401Unauthorized => "غير مصرّح",
            StatusCodes.Status403Forbidden => "لا تملك صلاحية لتنفيذ هذا الإجراء",
            StatusCodes.Status404NotFound => "العنصر غير موجود",
            StatusCodes.Status409Conflict => "يوجد سجل بنفس البيانات",
            StatusCodes.Status429TooManyRequests => "تم تجاوز عدد المحاولات المسموح، يرجى المحاولة لاحقاً",
            _ => "حدث خطأ"
        };
    };
});

var app = builder.Build();

// ── Database migration + first-run seed ───────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(scope.ServiceProvider, config, app.Logger);
}

// ── Optional PostgreSQL + pgvector index for the medical library ──
// Reindexing is incremental, so this only embeds new or changed files and then copies the vectors of
// everything already indexed into the store. When the store is not configured nothing happens here.
var vectors = app.Services.GetRequiredService<VectorStore>();
if (vectors.Enabled && await vectors.EnsureReadyAsync())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<MedicalLibraryService>().StartReindex();
}

// ── Pipeline (PLAN_ADDENDUM.md 7.6 / 7.7) ─────────────────
// Headers and the per-request correlation id come first, so even a failed response carries them; the exception
// handler right after, so no middleware or endpoint can ever leak a raw error to the client.
app.UseMiddleware<SecurityHeadersMiddleware>();
if (trustForwardedHeaders)
    // The defaults read X-Forwarded-For/-Proto/-Host/-Port from the trusted reverse proxy.
    app.UseForwardedHeaders();
if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseExceptionHandler("/api/error");

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
// PLAN.md 10.3: a suspended or expired clinic stays readable but can no longer write. Reads pass untouched; a
// refused write answers 403 with the Arabic explanation, which the UI shows instead of failing silently.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method)
        && await context.RequestServices.GetRequiredService<SubscriptionGuard>().IsLockedAsync(context.User))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            """{"message":"العيادة على وضع القراءة فقط — الاشتراك منتهي أو موقوف، راجع مالك المنصة لتجديد الاشتراك"}""");
        return;
    }
    await next();
});
app.UseAuthorization();

app.MapControllers();
app.MapHub<QueueHub>("/hubs/queue");
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// PLAN_ADDENDUM.md 7.7: any unhandled exception comes back as a clean ProblemDetails — a generic title in
// production (the full error, with its correlation id, stays in the log); in development the message helps.
app.Map("/api/error", async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    logger.LogError(error, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
    context.Response.Clear();
    // Clear() wipes the headers the SecurityHeadersMiddleware set earlier; put the baseline (and the correlation
    // id, the one that ties this failure to its log line) back before anything else is written.
    SecurityHeadersMiddleware.ApplyHeaders(context);
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";
    var problem = new ProblemDetails
    {
        Status = StatusCodes.Status500InternalServerError,
        Title = "خطأ غير متوقع في الخادم",
        Type = "https://httpwg.org/specs/rfc9457.html#internal.server.error",
        Instance = context.Request.Path
    };
    if (context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() && error?.Message is { Length: > 0 } message)
        problem.Detail = message;
    await context.Response.WriteAsJsonAsync(problem);
});

// The acceptance test for 7.7 needs an induced 500; this door exists outside production only.
if (!app.Environment.IsProduction())
    app.MapGet("/api/dev/crash", () =>
    {
        throw new InvalidOperationException("Induced test crash (PLAN_ADDENDUM.md 7.7).");
    });

app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// The integration test project boots this exact application through WebApplicationFactory,
/// which needs a public entry-point type (PLAN_ADDENDUM.md 0.1). Nothing else uses it.
/// </summary>
public partial class Program { }

