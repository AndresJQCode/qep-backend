using System.Threading.RateLimiting;
using Api;
using Bootstrapper;
using Bootstrapper.Authentication;
using Bootstrapper.Csrf;
using BuildingBlocks.Observability;
using Modules.Audit.Infrastructure;
using Modules.Catalog.Api;
using Modules.Catalog.Infrastructure;
using Modules.Companies.Api;
using Modules.Companies.Infrastructure;
using Modules.Customers.Api;
using Modules.Customers.Infrastructure;
using Modules.Geography.Api;
using Modules.Geography.Infrastructure;
using Modules.Identity.Infrastructure;
using Modules.Notifications.Infrastructure;
using Modules.Storage.Api;
using Modules.Storage.Infrastructure;
using Modules.Tenancy.Api;
using Modules.Tenancy.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddQepLogging();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddQepPlatform(
    builder.Configuration,
    builder.Environment);

// Superficies públicas/sin autenticar: ventana fija por IP, generosa para tráfico real
// pero acotada contra el abuso. Hoy está atada al documento OpenAPI y a la referencia de
// API de Scalar; atarla a todo endpoint público de lectura o webhook que se agregue.
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
// La defensa CSRF protege la sesión autenticada por cookie (ver AddAuthentication);
// el stub de desarrollo confía en los headers que manda el llamador en vez de en una
// cookie, así que acá no hay sesión que proteger ni superficie de ataque del navegador.
if (!QepAuthenticationMode.UseDevelopmentStub(builder.Configuration, builder.Environment))
{
    app.UseQepCsrfProtection();
}

app.UseAuthentication();
app.UseAuthorization();

// Se sirve en todos los entornos, Production incluido: la API desplegada está pensada
// para auto-documentarse. Los dos endpoints son anónimos y alcanzables desde internet
// —el ingress rutea "/" a este servicio sin filtrar path— así que el documento OpenAPI
// publica toda la superficie de la API (rutas, formas de request, códigos de error) a
// quien la pida. Limitada por IP para acotar el scraping. Para volver privada la
// referencia, envolver este bloque en una verificación de entorno o de autorización.
app.MapOpenApi()
    .AllowAnonymous()
    .RequireRateLimiting(RateLimiterPolicies.Public);
// Prefijo literal para que la referencia viva en /scalar/v1, la URL que abren los perfiles
// de lanzamiento. El default del paquete es /scalar; el prefijo rechaza un placeholder
// "{documentName}", y hay un único documento OpenAPI "v1" para servir.
app.MapScalarApiReference("/scalar/v1")
    .AllowAnonymous()
    .RequireRateLimiting(RateLimiterPolicies.Public);

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();
app.MapAuthSessionEndpoints();
app.MapAuthPreferenceEndpoints();
app.MapRegistrationEndpoints();
app.MapAuthorizationCatalogEndpoints();
app.MapTenantSettingsEndpoints();
app.MapMembershipEndpoints();
app.MapStorageEndpoints();
app.MapCatalogEndpoints();
app.MapCatalogTaxRateEndpoints();
app.MapCompanyEndpoints();
app.MapCustomerEndpoints();
app.MapGeographyEndpoints();

await app.Services.InitializeTenancyDatabaseAsync(
    app.Environment,
    app.Lifetime.ApplicationStopping);
// Después de Tenancy: Tenancy suelta la tabla de auditoría (DropAuditOwnership) antes de
// que la migración del módulo Audit pase a ser su única dueña (ADR 0019).
await app.Services.InitializeAuditDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeIdentityDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeNotificationsDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeStorageDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeCatalogDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeCustomersDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeCompaniesDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeGeographyDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.RunAsync();

public partial class Program;
