using System.Threading.RateLimiting;
using Api;
using Bootstrapper;
using Bootstrapper.Authentication;
using Bootstrapper.Cors;
using Bootstrapper.Csrf;
using Bootstrapper.Health;
using Bootstrapper.ReverseProxy;
using Bootstrapper.Seeding;
using BuildingBlocks.Observability;
using Modules.Audit.Infrastructure;
using Modules.Authorization.Infrastructure;
using Modules.Catalog.Api;
using Modules.Catalog.Infrastructure;
using Modules.Companies.Api;
using Modules.Companies.Infrastructure;
using Modules.Customers.Api;
using Modules.Customers.Infrastructure;
using Modules.Geography.Api;
using Modules.Geography.Infrastructure;
using Modules.Identity.Infrastructure;
using Modules.Integrations.Api;
using Modules.Integrations.Infrastructure;
using Modules.Messaging.Infrastructure;
using Modules.Notifications.Infrastructure;
using Modules.Platform.Api;
using Modules.Platform.Infrastructure;
using Modules.Pos.Api;
using Modules.Pos.Infrastructure;
using Modules.Quotations.Api;
using Modules.Quotations.Infrastructure;
using Modules.Reporting.Api;
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
builder.Services.AddQepHealthChecks(builder.Configuration);
builder.Services.AddQepForwardedHeaders(builder.Configuration);
builder.Services.AddQepCors(builder.Configuration);

// Superficies públicas/sin autenticar: ventana fija por IP del cliente —la que deja
// UseForwardedHeaders detrás del ingress, no la del nodo—, generosa para tráfico real
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
// Primero de todo, a propósito: reemplaza RemoteIpAddress por la IP del cliente que nginx anota en
// X-Real-IP (sólo si el par directo está en ForwardedHeaders:KnownNetworks; X-Forwarded-For se
// ignora), y todo lo que venga después tiene que ver esa y no la del nodo del ingress — la
// partición del rate limiter, la IP de la sesión y la de la auditoría del registro. Si la pusieras
// debajo del rate limiter, todo internet volvería a caer en el bucket de uno o dos nodos. Ver
// AddQepForwardedHeaders.
app.UseForwardedHeaders();
app.UseExceptionHandler();
// CORS para la SPA (Cors:AllowedOrigins; sin orígenes no se registra). Antes de la defensa CSRF,
// de autenticación y de autorización, a propósito: el preflight se contesta acá sin llegar a
// ellas, y las respuestas reales llevan Access-Control-Allow-Origin aunque sean un 401, un 403 o
// un 422 — sin ese header el navegador le esconde el cuerpo del error a la SPA. Los headers se
// aplican al empezar la respuesta, así que sobreviven a que UseExceptionHandler la limpie. Ver
// CorsSettings.
app.UseQepCors();
// Afuera de autenticacion y autorizacion a proposito: es la unica posicion desde la que se puede
// ver el 401 que escribe la primera y el 403 que escribe la segunda, que no pasan por el
// manejador de excepciones porque no tiran nada. Ver RequestFailureLoggingMiddleware.
app.UseMiddleware<RequestFailureLoggingMiddleware>();
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

// La liveness NO toca la base, a propósito: si dependiera de ella, una caída de PostgreSQL haría
// que el kubelet reiniciara todos los pods en cadena, y reiniciar no arregla la base. Esa
// dependencia la mira la readiness, que sólo saca al pod del Service hasta que la base vuelva.
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();
app.MapQepReadiness();
app.MapAuthSessionEndpoints();
app.MapAuthPreferenceEndpoints();
app.MapRegistrationEndpoints();
app.MapAuthorizationCatalogEndpoints();
app.MapTenantModulesEndpoints();
app.MapRoleEndpoints();
app.MapTenantSettingsEndpoints();
app.MapOperatorEndpoints();
app.MapMembershipEndpoints();
app.MapInvitationEndpoints();
app.MapStorageEndpoints();
app.MapCatalogEndpoints();
app.MapCatalogTaxRateEndpoints();
app.MapCompanyEndpoints();
app.MapCustomerEndpoints();
app.MapClientClassificationEndpoints();
app.MapGeographyEndpoints();
app.MapQuotationEndpoints();
app.MapOrderEndpoints();
app.MapOrdersExportLayoutEndpoints();
app.MapReportingEndpoints();
app.MapPlatformEndpoints();
app.MapPosEndpoints();
app.MapIntegrationsEndpoints();

await app.Services.InitializeTenancyDatabaseAsync(app.Lifetime.ApplicationStopping);
// Sin esto `authorization.roles` no existe, y como `TenantRoleCatalog` la consulta al
// resolver permisos, TODO request autenticado sale 500 — no sólo los de roles. Fue
// exactamente lo que rompió los tests de integración de Tenancy al agregar el módulo.
await app.Services.InitializeAuthorizationDatabaseAsync(
    app.Lifetime.ApplicationStopping);
// Después de Tenancy: Tenancy suelta la tabla de auditoría (DropAuditOwnership) antes de
// que la migración del módulo Audit pase a ser su única dueña (ADR 0019).
await app.Services.InitializeAuditDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeIdentityDatabaseAsync(
    app.Lifetime.ApplicationStopping);
// El esquema `platform` ya existia --lo usa la tabla outbox_messages, que crea BuildingBlocks--
// pero nadie lo tenia a cargo. Esta migracion solo crea request_failures; el orden respecto de
// los demas modulos no importa, porque la tabla no referencia a ninguna otra a proposito.
await app.Services.InitializePlatformDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeNotificationsDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeStorageDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeCatalogDatabaseAsync(
    app.Lifetime.ApplicationStopping);
// Antes de Customers: la migracion de Customers agrega una FK real
// (customers.customers.city_id -> geography.cities.id), asi que geography.cities tiene que
// existir cuando esa migracion corre. Ver el comentario en CustomersDbContext.ConfigureCustomer.
await app.Services.InitializeGeographyDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeCustomersDatabaseAsync(
    app.Lifetime.ApplicationStopping);
await app.Services.InitializeCompaniesDatabaseAsync(
    app.Lifetime.ApplicationStopping);
// Despues de Catalog y Customers: quotations referencia sus datos (producto, cliente) por id
// suelto, sin FK real -- no hay dependencia de orden estricta, pero se inicializa al final del
// grupo de modulos de negocio por consistencia con el resto de este archivo.
await app.Services.InitializeQuotationsDatabaseAsync(
    app.Lifetime.ApplicationStopping);
// Después de Companies: pos.cash_sessions lleva una FK real a companies.companies (spec,
// decisión 8), así que esa tabla tiene que existir cuando esta migración corre.
await app.Services.InitializePosDatabaseAsync(
    app.Lifetime.ApplicationStopping);

// Integrations (spec 2026-10-08): sin FKs a otros esquemas. Va después de Audit porque escribe en
// audit.entries, que crea la migración de Audit.
await app.Services.InitializeIntegrationsDatabaseAsync(
    app.Lifetime.ApplicationStopping);

// Messaging (spec 2026-10-09): después de Audit (escribe en audit.entries); no tiene FKs a otros
// esquemas: Integrations y Customers entran por puertos. Las extensiones (pg_trgm, unaccent,
// btree_gin) van en public, como las de Customers y Catalog.
await app.Services.InitializeMessagingDatabaseAsync(
    app.Lifetime.ApplicationStopping);

// Después de todas las migraciones: la semilla escribe en las tablas de cuatro módulos y
// necesita que existan. Apagada por defecto — ver SeedOptions.
await app.Services.RunQepSeedAsync(app.Lifetime.ApplicationStopping);

await app.RunAsync();

public partial class Program;
