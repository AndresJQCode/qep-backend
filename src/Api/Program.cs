using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Api;
using Bootstrapper;
using Bootstrapper.Authentication;
using Bootstrapper.Csrf;
using BuildingBlocks.Observability;
using Modules.Audit.Infrastructure;
using Modules.Identity.Infrastructure;
using Modules.Notifications.Infrastructure;
using Modules.Storage.Api;
using Modules.Storage.Infrastructure;
using Modules.Tenancy.Api;
using Modules.Tenancy.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddQepLogging();

// The "Local" environment runs the real Google JwtBearer scheme (not the dev stub)
// while still reading the Google client id from local user-secrets, so a developer
// can exercise the real login flow without committing any configuration.
if (builder.Environment.IsEnvironment("Local"))
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddQepPlatform(
    builder.Configuration,
    builder.Environment);

// Public/unauthenticated surfaces: per-IP fixed window, generous enough for real traffic
// but bounded against abuse. Currently attached to the OpenAPI document and the Scalar
// API reference; attach it to any further public read or webhook endpoint as it is added.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static FixedWindowRateLimiterOptions FixedWindow(string _) => new()
    {
        PermitLimit = 120,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
    options.AddPolicy(
        RateLimiterPolicies.Public,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: FixedWindow));
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
// The CSRF defense protects the cookie-authenticated session (see AddAuthentication);
// the dev-stub trusts caller-supplied headers instead of a cookie, so there is no
// session to protect and no browser-mediated attack surface to defend against here.
if (!QepAuthenticationMode.UseDevelopmentStub(builder.Configuration, builder.Environment))
{
    app.UseQepCsrfProtection();
}

app.UseAuthentication();
app.UseAuthorization();

// Served in every environment, Production included: the deployed API is meant to be
// self-documenting. Both endpoints are anonymous and internet-reachable — the ingress
// routes "/" to this service without any path filtering — so the OpenAPI document
// publishes the full API surface (routes, request shapes, error codes) to whoever asks.
// Rate-limited per IP to bound scraping. To take the reference private again, wrap this
// block in an environment or authorization check.
app.MapOpenApi()
    .AllowAnonymous()
    .RequireRateLimiting(RateLimiterPolicies.Public);
// Literal prefix so the reference lives at /scalar/v1, the URL the launch profiles
// open. The package default is /scalar; the prefix rejects a "{documentName}"
// placeholder, and there is only the single "v1" OpenAPI document to serve.
app.MapScalarApiReference("/scalar/v1")
    .AllowAnonymous()
    .RequireRateLimiting(RateLimiterPolicies.Public);

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();
app.MapAuthSessionEndpoints();
app.MapRegistrationEndpoints();
app.MapAuthorizationCatalogEndpoints();
app.MapTenantSettingsEndpoints();
app.MapMembershipEndpoints();
app.MapStorageEndpoints();

await app.Services.InitializeTenancyDatabaseAsync(
    app.Environment,
    app.Lifetime.ApplicationStopping);
// After Tenancy: Tenancy relinquishes the audit table (DropAuditOwnership) before the
// Audit module's migration becomes its sole owner (ADR 0019).
await app.Services.InitializeAuditDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeIdentityDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeNotificationsDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeStorageDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.RunAsync();

public partial class Program;
