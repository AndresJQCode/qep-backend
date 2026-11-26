using System.Threading.RateLimiting;
using Api;
using Bootstrapper;
using Bootstrapper.ApiKeys;
using Bootstrapper.Authentication;
using Bootstrapper.Cors;
using Bootstrapper.Csrf;
using Bootstrapper.Health;
using Bootstrapper.Realtime;
using Bootstrapper.ReverseProxy;
using Bootstrapper.Seeding;
using Bootstrapper.Startup;
using Bootstrapper.Valkey.RateLimiting;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Options;
using Modules.Ai.Api;
using Modules.Ai.Infrastructure;
using Modules.ApiAccess.Api;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Infrastructure;
using Modules.Audit.Infrastructure;
using Modules.Authorization.Infrastructure;
using Modules.Automations.Api;
using Modules.Automations.Infrastructure;
using Modules.Buyers.Api;
using Modules.Buyers.Infrastructure;
using Modules.Catalog.Api;
using Modules.Catalog.Infrastructure;
using Modules.Companies.Api;
using Modules.Companies.Infrastructure;
using Modules.Customers.Api;
using Modules.Customers.Infrastructure;
using Modules.Geography.Api;
using Modules.Geography.Infrastructure;
using Modules.Hr.Api;
using Modules.Hr.Infrastructure;
using Modules.Identity.Infrastructure;
using Modules.Integrations.Api;
using Modules.Integrations.Infrastructure;
using Modules.Loans.Api;
using Modules.Loans.Infrastructure;
using Modules.Messaging.Api;
using Modules.Messaging.Api.Realtime;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure;
using Modules.Notifications.Api;
using Modules.Notifications.Infrastructure;
using Modules.Payroll.Api;
using Modules.Payroll.Infrastructure;
using Modules.Pipeline.Api;
using Modules.Pipeline.Infrastructure;
using Modules.Platform.Api;
using Modules.Platform.Infrastructure;
using Modules.Pos.Api;
using Modules.Pos.Infrastructure;
using Modules.Purchases.Api;
using Modules.Purchases.Infrastructure;
using Modules.Quotations.Api;
using Modules.Quotations.Infrastructure;
using Modules.Reporting.Api;
using Modules.Scheduling.Api;
using Modules.Scheduling.Infrastructure;
using Modules.ServiceContracts.Api;
using Modules.ServiceContracts.Infrastructure;
using Modules.Shopify.Api;
using Modules.Shopify.Infrastructure;
using Modules.Siigo.Api;
using Modules.Siigo.Infrastructure;
using Modules.Storage.Api;
using Modules.Storage.Infrastructure;
using Modules.Store.Api;
using Modules.Store.Application;
using Modules.Store.Infrastructure;
using Modules.Suppliers.Api;
using Modules.Suppliers.Infrastructure;
using Modules.Support.Api;
using Modules.Support.Infrastructure;
using Modules.Tenancy.Api;
using Modules.Tenancy.Infrastructure;
using Modules.Wholesale.Api;
using Modules.Wholesale.Infrastructure;
using Modules.Wms.Api;
using Modules.Wms.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddQepLogging();
// Después de AddQepLogging, porque envuelve su proveedor: el token de una invitación no sale en el scope RequestPath de los
// logs ni en el url.path de los spans (SensitivePaths).
builder.Services.AddSensitivePathRedaction();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddQepPlatform(
    builder.Configuration,
    builder.Environment);
builder.Services.AddScoped<SessionEstablisher>();
builder.Services.AddWebTelemetry();
builder.Services.AddQepHealthChecks(builder.Configuration);
builder.Services.AddQepForwardedHeaders(builder.Configuration);
builder.Services.AddQepCors(builder.Configuration);
// Spec 2026-10-14 (Mensajería en tiempo real): SignalR y el sink que lleva al hub lo que Infrastructure escucha por NOTIFY.
builder.Services.AddMessagingRealtime();

// Superficies públicas/sin autenticar: ventana fija por IP del cliente —la que deja
// UseForwardedHeaders detrás del ingress, no la del nodo—, generosa para tráfico real
// pero acotada contra el abuso. Hoy está atada al documento OpenAPI y a la referencia de
// API de Scalar; atarla a todo endpoint público de lectura o webhook que se agregue.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Spec 2026-10-30 §6.1: solo la API externa lleva cuerpo y Retry-After en su 429 por IP; el resto queda como hoy.
    options.OnRejected = ExternalRateLimitRejection.WriteAsync;
    // Spec 2026-11-13, D-3: las siete ventanas fijas salen de SharedRateLimitPartitions, que las cuenta en memoria como
    // siempre o, con Valkey:Areas:RateLimiting, en Valkey para todos los pods. Los números siguen acá. El webhook no pasa
    // por ahí: es concurrencia y protege al pod.
    static SharedRateLimitPartitions Shared(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<SharedRateLimitPartitions>();
    options.AddPolicy(
        RateLimiterPolicies.Public,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.Public,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Endpoints de credenciales: 10 por minuto por IP y por endpoint. La clave usa el PATRÓN de ruta,
    // no el path, para que /invitations/{token}/accept-with-password no abra una partición por token.
    // En Valkey la partición entra hasheada (lleva el patrón de ruta).
    options.AddPolicy(
        RateLimiterPolicies.Credentials,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.Credentials,
            $"{(httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText}|{httpContext.Connection.RemoteIpAddress}",
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            },
            hashPartition: true));

    // Telemetría del navegador: 600 por minuto por IP. UseRateLimiter corre antes de UseAuthentication (más abajo),
    // así que acá no hay usuario ni tenant con qué particionar: lo único que se ve es la IP del cliente. 600 sobra para
    // una oficina entera detrás de una IP (la SPA manda por lotes, uno por cambio de pestaña o cada tanto), y acota a
    // quien quiera inflar las métricas. Sin IP (TestServer) todos comparten la partición "unknown", como en Public.
    options.AddPolicy(
        RateLimiterPolicies.Telemetry,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.Telemetry,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Spec 2026-10-09 §6.7 y §8.2: Meta manda desde pocas IPs compartidas y en ráfagas; con Public un
    // 429 la haría reintentar hasta 7 días. Una sola partición: lo que se protege es el pod. Los valores
    // salen de MessagingWebhookOptions ya bindeadas y validadas al arrancar (> 0); la fábrica de la
    // partición corre una sola vez, con el primer request.
    options.AddPolicy(
        RateLimiterPolicies.Webhook,
        httpContext => RateLimitPartition.GetConcurrencyLimiter(
            partitionKey: "webhook",
            factory: _ =>
            {
                var webhook = httpContext.RequestServices.GetRequiredService<IOptions<MessagingWebhookOptions>>().Value;
                return new ConcurrencyLimiterOptions
                {
                    PermitLimit = webhook.ConcurrencyLimit,
                    QueueLimit = webhook.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                };
            }));

    // Spec 2026-10-30 §6.1: la API externa, 600 por minuto por IP y antes de autenticar, para frenar claves inventadas. El
    // límite por clave (300) corre después, como filtro del grupo. Se particiona por IP y nunca por el header.
    options.AddPolicy(
        RateLimiterPolicies.External,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.External,
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = httpContext.RequestServices.GetRequiredService<IOptions<ApiAccessOptions>>().Value.RateLimit.PerIpPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Tienda pública: la cubeta es la IP del visitante, no la del servidor del storefront (si no, todos los visitantes
    // compartirían una). StorefrontClient.VisitorIp solo cree el header X-Qep-Visitor-Ip con la clave válida; sin ella
    // cae a la IP del par directo, así que quien no tiene la clave no escapa de su cubeta cambiando el header. Corre antes
    // del filtro de la clave: los intentos con clave inventada también gastan el cupo de su IP.
    options.AddPolicy(
        RateLimiterPolicies.StorefrontRead,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.StorefrontRead,
            StorefrontClient.VisitorIp(httpContext, StorefrontSecret(httpContext)),
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(
        RateLimiterPolicies.StorefrontSubmit,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.StorefrontSubmit,
            StorefrontClient.VisitorIp(httpContext, StorefrontSecret(httpContext)),
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));

    // Credenciales de compradores: 10 por minuto por ruta y por IP del visitante. La clave usa el PATRÓN de ruta (no el path) para que
    // cada ruta lleve su contador y el host de la tienda no abra una partición por tienda. En Valkey la partición entra hasheada.
    options.AddPolicy(
        RateLimiterPolicies.StorefrontCredentials,
        httpContext => Shared(httpContext).FixedWindow(
            RateLimiterPolicies.StorefrontCredentials,
            $"{(httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText}|{StorefrontClient.VisitorIp(httpContext, StorefrontSecret(httpContext))}",
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            },
            hashPartition: true));

    // Formulario público de Soporte (plan F6): el navegador llama directo, sin BFF, así que la cubeta es RemoteIpAddress, que
    // UseForwardedHeaders ya corrigió con X-Real-IP detrás del ingress. Más estrictos que Public: radicar y consultar son el blanco de
    // abuso (spam de tickets y fuerza bruta del PIN, que además frena el bloqueo por radicado de SupportTicketAccess).
    options.AddPolicy(
        RateLimiterPolicies.SupportPublicRead,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(
        RateLimiterPolicies.SupportPublicSubmit,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));
    options.AddPolicy(
        RateLimiterPolicies.SupportPublicLookup,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    // Plan F33: los adjuntos no piden Turnstile, así que el limitador es lo único que frena a quien abre sesiones de subida en serie.
    options.AddPolicy(
        RateLimiterPolicies.SupportPublicUpload,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));

    static string? StorefrontSecret(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IOptions<StoreOptions>>().Value.Storefront.SharedSecret;
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
app.UseMiddleware<ExternalApiCallLoggingMiddleware>();
// Afuera de autenticacion y autorizacion a proposito: es la unica posicion desde la que se puede
// ver el 401 que escribe la primera y el 403 que escribe la segunda, que no pasan por el
// manejador de excepciones porque no tiran nada. Ver RequestFailureLoggingMiddleware.
app.UseMiddleware<RequestFailureLoggingMiddleware>();
// Antes del limitador, a propósito: así el 429 (y el 400/413 del cuerpo, el 401 de la clave y lo que sale por excepción) de /buyer/*
// y del formulario público de Soporte también llevan Cache-Control: no-store. Ver BuyerNoStoreMiddleware.
app.UseMiddleware<BuyerNoStoreMiddleware>();
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
// Junto a los health checks y no con los módulos: es infraestructura, como ellos. Ver VersionEndpoints.
app.MapVersionEndpoint();
app.MapAuthSessionEndpoints();
app.MapAuthPreferenceEndpoints();
app.MapRegistrationEndpoints();
app.MapPasswordAuthEndpoints();
app.MapAuthorizationCatalogEndpoints();
app.MapWebTelemetryEndpoints();
app.MapTenantModulesEndpoints();
app.MapRoleEndpoints();
app.MapTenantSettingsEndpoints();
app.MapCurrencyEndpoints();
app.MapOperatorEndpoints();
app.MapMembershipEndpoints();
app.MapInvitationEndpoints();
app.MapStorageEndpoints();
app.MapCatalogEndpoints();
app.MapCatalogTaxRateEndpoints();
app.MapCatalogOptionEndpoints();
app.MapCatalogCategoryEndpoints();
app.MapCatalogPriceListEndpoints();
app.MapCompanyEndpoints();
app.MapCustomerEndpoints();
app.MapClientClassificationEndpoints();
app.MapCustomerEngagementEndpoints();
app.MapGeographyEndpoints();
// Spec 2026-10-26 §6: la consola de ciudades sin resolver y sinónimos del operador.
app.MapCityOperatorEndpoints();
// Spec 2026-11-10: la consola de la unidad tributaria del operador.
app.MapTaxUnitOperatorEndpoints();
// Spec 2026-11-22: la consola de valores legales de nómina del operador.
app.MapLegalParameterOperatorEndpoints();
app.MapQuotationEndpoints();
app.MapOrderEndpoints();
app.MapOrdersExportLayoutEndpoints();
app.MapOrderSettingsEndpoints();
app.MapQuotationSettingsEndpoints();
app.MapWhatsAppChannelEndpoints();
app.MapCarrierEndpoints();
app.MapReportingEndpoints();
app.MapPlatformEndpoints();
app.MapPosEndpoints();
app.MapIntegrationsEndpoints();
// Spec 2026-10-15 §4.3: el inicio anónimo de la instalación de Shopify, con el limitador por IP de las superficies públicas.
app.MapShopifyInstallEndpoints(RateLimiterPolicies.Public);
app.MapWhatsAppWebhook(RateLimiterPolicies.Webhook);
// Spec 2026-10-14 §6.2: el webhook de Instagram, con el mismo limitador y la misma exención de CSRF (prefijo /api/webhooks/).
app.MapInstagramWebhook(RateLimiterPolicies.Webhook);
// Spec 2026-10-15 §5.1: el webhook de Shopify, con el mismo limitador y la misma exención de CSRF (prefijo /api/webhooks/).
app.MapShopifyWebhook(RateLimiterPolicies.Webhook);
// Spec 2026-10-15 §7: las rutas por tenant de Shopify: la configuración de la tienda, la bandeja y «Sincronizar ahora».
app.MapShopifyEndpoints();
// Spec 2026-11-13: las asociaciones de líneas personalizadas de cada tienda (nombre exacto → variante de Catalog).
app.MapShopifyCustomLineMappingEndpoints();
// Spec 2026-11-17 §7: productos desde Shopify (importar, la importación, la cobertura y desvincular).
app.MapShopifyProductEndpoints();
// Spec 2026-11-14 §6: los cambios de Shopify sobre pedidos importados («Cambios por revisar», aceptar, rechazar y revisar).
app.MapShopifyOrderEditEndpoints();
// Spec 2026-11-18: Siigo (configuración por conexión y factura electrónica de un pedido).
app.MapSiigoEndpoints();
app.MapMessagingEndpoints();
// Spec 2026-10-30 §5.2: la API externa, autenticada con X-Api-Key. Después de Messaging, de cuyos handlers depende.
app.MapExternalApi();
app.MapMessagingHub();
// Spec 2026-11-15 §5.1: the global hub; each module authorizes its own topics.
app.MapAppHub();
app.MapPipelineEndpoints();
// Spec 2026-10-21 §4.4: el constructor de chatbots.
app.MapAutomationsEndpoints();
// Spec 2026-10-29 y plan 2026-11-12: los agentes IA, con el proveedor que elige el tenant.
app.MapAiEndpoints();
app.MapSupplierEndpoints();
// Spec 2026-11-17: órdenes de compra a proveedores.
app.MapPurchaseOrderEndpoints();
// Spec 2026-11-17: mayorista (listas de precios, distribuidores y la invitación desde el link).
app.MapWholesaleEndpoints();
app.MapWholesaleInvitationEndpoints();
// Spec 2026-10-30: administración de claves de API.
app.MapApiKeyEndpoints();
app.MapServiceContractsEndpoints();
app.MapLoansEndpoints();
// Spec 2026-11-20: recursos humanos (empleados, contratos y su configuración).
app.MapHrEndpoints();
app.MapPayrollEndpoints();
app.MapSchedulingEndpoints();
// Spec 2026-11-14: soporte (tickets y PQRSF).
app.MapSupportEndpoints();
// Spec 2026-11-22: la campana de avisos de cada miembro.
app.MapNotificationEndpoints();
// Spec 2026-11-03: la tienda en línea (backoffice y rutas públicas).
app.MapStoreEndpoints();
// Tienda pública: sin sesión ni CSRF, con la clave del storefront y limitadores por IP del visitante.
app.MapPublicStoreEndpoints(RateLimiterPolicies.StorefrontRead, RateLimiterPolicies.StorefrontSubmit);
// Cuentas de comprador (spec 2026-11-07): mismo prefijo y misma clave del storefront; las credenciales llevan su propio cupo por ruta.
app.MapPublicBuyerEndpoints(RateLimiterPolicies.StorefrontRead, RateLimiterPolicies.StorefrontCredentials);
// Formulario público de Soporte (spec 2026-11-14 §9): anónimo, sin CSRF, con su CORS sin credenciales y limitadores por IP.
app.MapPublicSupportEndpoints(
    RateLimiterPolicies.SupportPublicRead,
    RateLimiterPolicies.SupportPublicSubmit,
    RateLimiterPolicies.SupportPublicLookup,
    RateLimiterPolicies.SupportPublicUpload);
// Spec WMS 2026-10-31: bodegas, ubicaciones, dueños, unidades y perfiles de variante.
app.MapWmsEndpoints();

// Spec 2026-10-29 (réplicas múltiples), D-2: con más de un pod, dos arranques a la vez migrarían y sembrarían a la vez.
// Todo el bloque —las migraciones y la semilla— corre bajo pg_advisory_xact_lock(StartupLock.Namespace, StartupLock.Key),
// tomado en una conexión directa (la regla del listener: QepDatabaseListener o QepDatabase) y sin pool. Es de transacción
// y no de sesión: no hay unlock que olvidar, y si el pod muere a mitad del bloque Postgres lo suelta al cerrar la conexión.
// Cada migración y la semilla siguen en sus propias conexiones: el candado es un semáforo, no una unidad de trabajo.
await app.Services.RunUnderStartupLockAsync(async (services, cancellationToken) =>
{
    await services.InitializeTenancyDatabaseAsync(cancellationToken);
    // Sin esto `authorization.roles` no existe, y como `TenantRoleCatalog` la consulta al
    // resolver permisos, TODO request autenticado sale 500 — no sólo los de roles. Fue
    // exactamente lo que rompió los tests de integración de Tenancy al agregar el módulo.
    await services.InitializeAuthorizationDatabaseAsync(
        cancellationToken);
    // Después de Tenancy: Tenancy suelta la tabla de auditoría (DropAuditOwnership) antes de
    // que la migración del módulo Audit pase a ser su única dueña (ADR 0019).
    await services.InitializeAuditDatabaseAsync(
        cancellationToken);
    await services.InitializeIdentityDatabaseAsync(
        cancellationToken);
    // El esquema `platform` ya existia --lo usa la tabla outbox_messages, que crea BuildingBlocks--
    // pero nadie lo tenia a cargo. Esta migracion solo crea request_failures; el orden respecto de
    // los demas modulos no importa, porque la tabla no referencia a ninguna otra a proposito.
    await services.InitializePlatformDatabaseAsync(
        cancellationToken);
    await services.InitializeNotificationsDatabaseAsync(
        cancellationToken);
    await services.InitializeStorageDatabaseAsync(
        cancellationToken);
    await services.InitializeCatalogDatabaseAsync(
        cancellationToken);
    // Antes de Customers: la migracion de Customers agrega una FK real
    // (customers.customers.city_id -> geography.cities.id), asi que geography.cities tiene que
    // existir cuando esa migracion corre. Ver el comentario en CustomersDbContext.ConfigureCustomer.
    await services.InitializeGeographyDatabaseAsync(
        cancellationToken);
    await services.InitializeCustomersDatabaseAsync(
        cancellationToken);
    await services.InitializeCompaniesDatabaseAsync(
        cancellationToken);
    // Después de Geography: suppliers.suppliers lleva una FK real a geography.cities.
    await services.InitializeSuppliersDatabaseAsync(
        cancellationToken);
    // Despues de Catalog, Customers y Companies, y el orden es estricto: quotations referencia sus datos
    // (producto, cliente) por id suelto, sin FK real, pero dos migraciones de impuestos por tipo leen y escriben esos
    // esquemas: AddQuotationWithholdings copia el pais de la empresa desde companies.companies, y
    // MigrateRetentionToWithholdings (§11.2) escribe en catalog.tax_rates y customers.customer_tax_rates. Tienen que
    // existir con su forma final cuando corren: si faltan, el guard de to_regclass las deja sin hacer nada y no vuelven a
    // correr.
    await services.InitializeQuotationsDatabaseAsync(
        cancellationToken);
    // Después de Companies: pos.cash_sessions lleva una FK real a companies.companies (spec,
    // decisión 8), así que esa tabla tiene que existir cuando esta migración corre.
    await services.InitializePosDatabaseAsync(
        cancellationToken);

    // Integrations (spec 2026-10-08): sin FKs a otros esquemas. Va después de Audit porque escribe en
    // audit.entries, que crea la migración de Audit.
    await services.InitializeIntegrationsDatabaseAsync(
        cancellationToken);

    // Messaging (spec 2026-10-09): después de Audit (escribe en audit.entries); no tiene FKs a otros
    // esquemas: Integrations y Customers entran por puertos. Las extensiones (pg_trgm, unaccent,
    // btree_gin) van en public, como las de Customers y Catalog.
    await services.InitializeMessagingDatabaseAsync(
        cancellationToken);

    // Pipeline (spec 2026-10-13): después de Audit (escribe en audit.entries) y de Tenancy (lee platform.outbox_messages). Sin FKs
    // a otros esquemas: clientes, conversaciones, membresías y cotizaciones son referencias blandas.
    await services.InitializePipelineDatabaseAsync(
        cancellationToken);

    // Shopify (spec 2026-10-15 §9): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas: conexión, cliente,
    // pedido, asesor y empresa son referencias blandas.
    await services.InitializeShopifyDatabaseAsync(
        cancellationToken);

    // Siigo (spec 2026-11-18): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas: conexión, pedido, cliente,
    // producto y empresa son referencias blandas.
    await services.InitializeSiigoDatabaseAsync(
        cancellationToken);

    // Automations (spec 2026-10-21 §4.1): después de Messaging e Integrations, cuyas conexiones y conversaciones referencia sin FK, y de
    // Audit (escribe en audit.entries).
    await services.InitializeAutomationsDatabaseAsync(
        cancellationToken);

    // Ai (spec 2026-10-29 §4.1): después de Messaging (escribe en messaging.connection_bots desde Bootstrapper) e Integrations, cuyas
    // conexiones referencia sin FK, y de Audit (escribe en audit.entries).
    await services.InitializeAiDatabaseAsync(
        cancellationToken);

    // ServiceContracts (spec 2026-10-29 §2): sin FKs a otros esquemas; clientes y membresías son referencias blandas.
    await services.InitializeServiceContractsDatabaseAsync(
        cancellationToken);

    // Loans (spec 2026-10-31): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas; clientes y membresías son
    // referencias blandas.
    await services.InitializeLoansDatabaseAsync(
        cancellationToken);

    // Hr (spec 2026-11-20): after Audit (writes audit.entries) and after Companies (hr.sites and hr.employment_contracts have FKs to
    // companies.companies). Memberships, cities, countries and files are soft references.
    await services.InitializeHrDatabaseAsync(
        cancellationToken);

    // Payroll (spec 2026-11-21): after Audit (writes audit.entries) and after Companies (payroll.periods and payroll.company_settings
    // have FKs to companies.companies). Hr is read through ports only.
    await services.InitializePayrollDatabaseAsync(
        cancellationToken);

    // Scheduling (spec 2026-11-21): sin llaves foráneas a otros módulos (clientes, miembros, conversaciones y plantillas son referencias
    // blandas), pero detrás de Hr por orden de despliegue; crea btree_gist para la restricción de exclusión de las sesiones.
    await services.InitializeSchedulingDatabaseAsync(
        cancellationToken);

    // Support (spec 2026-11-14): después de Audit (escribe en audit.entries) y de Companies: support.form_versions lleva una FK real a
    // companies.companies (spec 2026-11-15 §9.3, plan L29), así que esa tabla tiene que existir cuando esta migración corre. Clientes y
    // membresías siguen siendo referencias blandas.
    await services.InitializeSupportDatabaseAsync(
        cancellationToken);

    // Store (spec 2026-11-03): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas; tenant, empresa, categorías,
    // clientes, pedidos y membresías son referencias blandas.
    await services.InitializeStoreDatabaseAsync(
        cancellationToken);

    // Purchases (spec 2026-11-17): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas: proveedor, variante y
    // membresías son referencias blandas.
    await services.InitializePurchasesDatabaseAsync(
        cancellationToken);

    // Wholesale (spec 2026-11-17): después de Audit. Sin FKs a otros esquemas: listas, miembros, empresas, tenants y clientes
    // son referencias blandas.
    await services.InitializeWholesaleDatabaseAsync(cancellationToken);

    // Buyers (spec 2026-11-07): esquema propio, sin FKs a otros esquemas. Escribe en platform.outbox_messages (proyección).
    await services.InitializeBuyersDatabaseAsync(cancellationToken);

    // ApiAccess (spec 2026-10-30 §4.1): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas; tenant y
    // membresías son referencias blandas.
    await services.InitializeApiAccessDatabaseAsync(
        cancellationToken);

    // Wms (spec WMS 2026-10-31): después de Audit (escribe en audit.entries). Sin FKs a otros esquemas; variantes, ciudades y
    // proveedores son referencias blandas.
    await services.InitializeWmsDatabaseAsync(
        cancellationToken);

    // Después de todas las migraciones: la semilla escribe en las tablas de cuatro módulos y
    // necesita que existan. Apagada por defecto — ver SeedOptions.
    await services.RunQepSeedAsync(cancellationToken);

    // Spec 2026-11-25 (roles por tenant): después de la semilla, que crea tenants. Siembra los roles de las plantillas en todo
    // tenant sin la marca; si falla, el arranque falla.
    await services.SeedTenantRolesAsync(cancellationToken);
}, app.Lifetime.ApplicationStopping);

await app.RunAsync();

public partial class Program;
