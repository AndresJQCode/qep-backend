using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Customers.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Shopify.Api;
using Modules.Shopify.Infrastructure.Workers;
using Modules.Tenancy.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Shopify (spec 2026-10-15), copia de <c>PipelineApiHarness</c>: contenedor compartido, plantilla
/// migrada, una base por prueba y un host que fija sus claves con <c>UseSetting</c>; nunca las hereda de los user-secrets de quien
/// corre las pruebas.
/// </summary>
internal static class ShopifyApiHarness
{
    /// <summary>El client secret de la app en las pruebas: con él firma <see cref="Sign"/>, como Shopify con el real.</summary>
    public const string WebhookSecret = "shopify-test-secret";

    public const string Shop = "mi-tienda.myshopify.com";

    /// <summary>Lo que hace falta para sembrar el mundo de una tienda por las APIs de cada módulo.</summary>
    public static readonly string[] SeedPermissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        CompaniesPermissions.CompanyRead,
        CompaniesPermissions.CompanyManage,
        CustomersPermissions.ClassificationRead,
        CustomersPermissions.ClassificationManage,
        TenancyPermissions.SettingsUpdate,
    ];

    private const string TemplateDatabase = "qep_template_shopify";

    private static readonly string[] AdvisorRole = ["advisor"];

    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    /// <summary>Una base limpia y migrada para esta prueba. Se borra en <c>DisposeAsync</c>.</summary>
    public static async Task<TestDatabase> StartDatabaseAsync()
    {
        var server = await SharedServer.Value;
        var name = $"t_{Guid.CreateVersion7():N}";
        await CloneGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        }
        finally
        {
            CloneGate.Release();
        }

        return new TestDatabase(server, name);
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .WithCommand("-c", "max_connections=400")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar: arrancarlo una vez contra la plantilla la deja con todo.
        using (var factory = new QepApiFactory(ConnectionStringFor(server, TemplateDatabase)))
        using (factory.CreateClient())
        {
        }

        // Una conexión viva a la plantilla haría fallar todos los clones.
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(server, $"ALTER DATABASE \"{TemplateDatabase}\" WITH ALLOW_CONNECTIONS false");
        return server;
    }

    private static string ConnectionStringFor(PostgreSqlContainer server, string database) =>
        new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task ExecuteAdminAsync(PostgreSqlContainer server, string sql)
    {
        await using var connection = new NpgsqlConnection(server.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>La base de una prueba.</summary>
    public sealed class TestDatabase(PostgreSqlContainer server, string name) : IAsyncDisposable
    {
        public string GetConnectionString() => ConnectionStringFor(server, name);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(GetConnectionString()));
            await ExecuteAdminAsync(server, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        /// <summary>Ajuste opcional del contenedor para una prueba (los puertos que todavía no tienen adaptador, un doble con
        /// compuerta).</summary>
        public Action<IServiceCollection>? ConfigureTestServices { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // El host registra el worker de entregas y el de la conciliación. En las pruebas no corre solo: la que lo necesita lo corre a mano
            // (DrainAsync) y decide cuándo, así que uno en segundo plano reclamaría la misma entrega a la vez.
            builder.ConfigureTestServices(services =>
            {
                // La conciliación tampoco: la prueba que la necesita la corre a mano (RunOnceAsync). Ni el consumidor de los
                // eventos de sinónimos (ProcessBatchAsync), ni el worker que corrige (DrainAsync).
                foreach (var worker in services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && (descriptor.ImplementationType == typeof(ShopifyWebhookDeliveryWorker)
                            || descriptor.ImplementationType == typeof(ShopifyReconcileWorker)
                            || descriptor.ImplementationType == typeof(CitySynonymEventsWorker)
                            || descriptor.ImplementationType == typeof(ShopifyCityCorrectionWorker)))
                    .ToArray())
                {
                    services.Remove(worker);
                }
            });
            if (ConfigureTestServices is { } configure)
            {
                builder.ConfigureTestServices(configure);
            }

            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 40 }.ConnectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados (mismo criterio que PipelineApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Los workers de Messaging no corren solos durante una prueba de Shopify.
            builder.UseSetting("Messaging:Workers:DeliveryPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:MediaPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:CampaignPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:PurgeIntervalHours", "24");
            // Fuera de Production, sin el par de la app el proveedor shopify sale del catálogo; se fija para que las
            // pruebas del módulo vean el mismo catálogo que producción.
            builder.UseSetting("Shopify:App:ClientId", "shopify-test-client");
            builder.UseSetting("Shopify:App:ClientSecret", WebhookSecret);
            // El enlace guarda el token de la tienda cifrado: sin llave, LinkStoreAsync no puede crear la conexión. Calculada, no
            // escrita, como en IntegrationsApiHarness.
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
        }
    }

    private static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    /// <summary>
    /// Auth real con el stub apagado, como corre producción: así se registra <c>RequireCsrfHeaderMiddleware</c>, que en modo stub ni
    /// existe (CLAUDE.md). Ningún request de esta variante se autentica: sirve para las rutas anónimas como el webhook.
    /// </summary>
    public static WebApplicationFactory<Program> WithRealAuthentication(this WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:UseDevelopmentStub", "false");
            builder.UseSetting("Authentication:Audience", "qep-shopify-tests");
        });

    /// <summary>§5.1: HMAC-SHA256 de los bytes crudos con el client secret, en base64.</summary>
    public static string Sign(byte[] body, string secret = WebhookSecret) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Un webhook como lo manda Shopify, sin <c>X-Qep-Client</c>. <paramref name="signature"/> nulo = sin cabecera de
    /// firma; por defecto, la firma correcta.</summary>
    public static Task<HttpResponseMessage> PostWebhookAsync(
        HttpClient client, string topic, string webhookId, byte[] body, string? signature = "", string shop = Shop)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, ShopifyWebhookEndpoints.Route) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Shopify-Topic", topic);
        request.Headers.TryAddWithoutValidation("X-Shopify-Shop-Domain", shop);
        request.Headers.TryAddWithoutValidation("X-Shopify-Webhook-Id", webhookId);
        request.Headers.TryAddWithoutValidation("X-Shopify-API-Version", "2026-10");
        request.Headers.TryAddWithoutValidation("X-Shopify-Event-Id", Guid.CreateVersion7().ToString());
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Shopify-Hmac-Sha256", signature.Length == 0 ? Sign(body) : signature);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Un pedido REST de los fixtures de las pruebas unitarias (sintéticos hasta V-9), copiados al compilar.</summary>
    public static byte[] OrderFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rest", name + ".json"));

    /// <summary>El mismo pedido en GraphQL (la conciliación), como lo devuelve <c>order(id:)</c>.</summary>
    public static System.Text.Json.Nodes.JsonObject GraphQlFixture(string name) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphql", name + ".json")))!;

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal sealed record RegisteredTenant(Guid TenantId, Guid OwnerUserId, HttpClient Seeder);

    /// <summary>Copia de <c>PosApiHarness.RegisterTenantAsync</c>, en Bogotá.</summary>
    public static async Task<RegisteredTenant> RegisterTenantAsync(WebApplicationFactory<Program> factory)
    {
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", $"owner-{Guid.CreateVersion7():N}@example.com");
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Shopify Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisteredDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return new RegisteredTenant(
            registered.TenantId, registered.OwnerUserId, CreateClient(factory, registered.OwnerUserId, registered.TenantId, SeedPermissions));
    }

    /// <summary>El signup ya lo prende (<c>DefaultForNewTenants</c>, D-S20), pero sólo mientras
    /// <c>Entitlements:GrantDefaultModulesOnSignup</c> esté en <c>true</c>; la prueba lo fija por SQL, como <c>pos</c>, para no
    /// depender de esa clave. También lo vuelve a prender después de apagarlo.</summary>
    public static Task EnableShopifyAsync(string connectionString, Guid tenantId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'shopify', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId));

    /// <summary>
    /// La conexión y la ruta de una tienda, creadas por los servicios de Integrations (repositorio, rutas, cifrado y unidad de
    /// trabajo) como las deja <c>LinkShopifyStore</c>, sin pasar por el OAuth: el token es un sentinela.
    /// </summary>
    public static async Task<Guid> LinkStoreAsync(
        WebApplicationFactory<Program> factory, Guid tenantId, Guid createdBy, string shop = Shop, string accessToken = "shpat_sentinel_token")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var protector = services.GetRequiredService<ISecretProtector>();
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Shopify,
            tenantId,
            "Mi tienda",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ShopifyFieldKeys.ShopDomain] = shop,
                [ShopifyFieldKeys.ShopId] = "954889",
                [ShopifyFieldKeys.Scopes] = "read_orders,read_customers",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { [ShopifyFieldKeys.AccessToken] = accessToken },
            protector.Protect,
            createdBy,
            services.GetRequiredService<IClock>().UtcNow);
        services.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        services.GetRequiredService<IConnectionRouteRepository>().Add(
            IntegrationConnectionRoute.Create(IntegrationProviders.Shopify.Key, shop, accountId: null, tenantId, connection.Id));
        await services.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    /// <summary>Copia de <c>ReportingApiHarness.InviteActiveAdvisorAsync</c>: la membresía activa a cuyo nombre entran los pedidos.</summary>
    public static async Task<Guid> InviteActiveAdvisorAsync(WebApplicationFactory<Program> factory, string connectionString, RegisteredTenant tenant)
    {
        // Sin X-Permissions el stub concede los de tenancy, que traen advisorship.invite.
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId);
        var invited = await owner.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/memberships",
            new { email = $"advisor-{Guid.CreateVersion7():N}@example.com", displayName = "Asesora Shopify", roles = AdvisorRole },
            TestContext.Current.CancellationToken);
        invited.EnsureSuccessStatusCode();
        var membership = await invited.Content.ReadFromJsonAsync<MembershipDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        var payload = await ScalarAsync<string>(
            connectionString,
            """
            SELECT payload::text FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1' AND payload::text LIKE '%' || @membershipId || '%'
            ORDER BY occurred_at DESC LIMIT 1
            """,
            ("membershipId", membership.Id.ToString()));
        using var document = JsonDocument.Parse(payload);
        var token = document.RootElement.GetProperty("token").GetString();
        using var accepting = CreateClient(factory, membership.UserId, tenant.TenantId);
        var accepted = await accepting.PostAsync($"/api/v1/invitations/{token}/accept", content: null, TestContext.Current.CancellationToken);
        accepted.EnsureSuccessStatusCode();
        return membership.Id;
    }

    internal sealed record CompanyRef(Guid Id, string BankName, string AccountNumber);

    /// <summary>La empresa con su cuenta de cobro en COP, la que configura la tienda (§6.1).</summary>
    public static async Task<CompanyRef> CreateCompanyAsync(HttpClient client, Guid tenantId)
    {
        var accountNumber = $"{Random.Shared.Next(100000000, 999999999)}";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Origen Botánico SAS",
                bankAccounts = new[] { new { bankName = "Bancolombia", accountNumber, currency = "COP" } },
                taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2",
                cityId = await AnyCityIdAsync(client),
                phone = "6045551234",
                email = "ventas@origen.example.co",
                address = "Cra 50 # 10-20, Rionegro",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return new CompanyRef((await ReadIdAsync(response)), "Bancolombia", accountNumber);
    }

    public static async Task<Guid> CreateClassificationAsync(HttpClient client, Guid tenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/customers/classifications",
            new { name = "Tienda en línea", prefix = "WEB" },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    public static async Task<Guid> CreateTaxRateAsync(HttpClient client, Guid tenantId, string name, int percentage)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates", new { name, percentage }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    /// <summary>§6.2.2: sin la tasa del envío, un envío cobrado en COP espera con <c>freight_tax_rate_missing</c>.</summary>
    public static async Task SetFreightTaxRateAsync(HttpClient client, Guid tenantId, Guid taxRateId)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers/freight-tax-rate", new { taxRateId }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Producto con precio COP de lista y las escalas por defecto de <c>PosApiHarness</c>; el código es el SKU de Shopify.</summary>
    public static async Task<Guid> CreateProductAsync(HttpClient client, Guid tenantId, string code, string name, decimal priceCop, Guid? taxRateId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            // Variantes (Catalog, Tarea 5): código y precio van en la única variante.
            new
            {
                name,
                taxRateId,
                variants = new[]
                {
                    new
                    {
                        code,
                        pricing = new
                        {
                            prices = new Dictionary<string, decimal> { ["COP"] = priceCop },
                            scales = new object[]
                            {
                                new { fromUnit = 1, toUnit = 9, discount = 0m, restriction = "multiple", multiple = 1 },
                                new { fromUnit = 10, toUnit = 19, discount = 5m, restriction = "multiple", multiple = 1 },
                                new { fromUnit = 20, toUnit = 999_999, discount = 10m, restriction = "multiple", multiple = 1 },
                            },
                            packagingUnits = Array.Empty<int>(),
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadIdAsync(response);
    }

    private static async Task<Guid> AnyCityIdAsync(HttpClient client)
    {
        var departments = await client.GetFromJsonAsync<List<IdDto>>("/api/v1/departments", TestContext.Current.CancellationToken);
        Assert.NotNull(departments);
        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<IdDto>>(
                $"/api/v1/cities?departmentId={department.Id}", TestContext.Current.CancellationToken);
            if (cities is { Count: > 0 })
            {
                return cities[0].Id;
            }
        }

        throw new InvalidOperationException("No seeded DIVIPOLA department has at least one city.");
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
    }

    private sealed record RegisteredDto(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipDto(Guid Id, Guid UserId);

    private sealed record IdDto(Guid Id);
}

/// <summary>Guarda en memoria lo que el host loguea, para afirmar sobre los avisos (y que no lleven el cuerpo).</summary>
internal sealed class RecordingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Message)> entries = new();

    public IReadOnlyList<(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries => [.. entries];

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class Logger(
        string category,
        System.Collections.Concurrent.ConcurrentQueue<(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Message)> entries)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}

/// <summary>
/// El tenant de una tienda lista para importar: <c>shopify</c> prendido, la tienda enlazada (conexión y ruta), un asesor activo, la
/// empresa con cuenta COP, una clasificación, la tasa del envío y los productos de los SKU de los fixtures. Lo usan las pruebas del
/// worker y las del flujo de punta a punta.
/// </summary>
internal sealed record ShopifyWorld(
    ShopifyApiHarness.RegisteredTenant Tenant,
    Guid ConnectionId,
    Guid AdvisorMembershipId,
    ShopifyApiHarness.CompanyRef Company,
    Guid ClassificationId,
    Guid Tax19,
    Guid Camiseta,
    Guid Gorra)
{
    public Guid TenantId => Tenant.TenantId;

    public static async Task<ShopifyWorld> ArrangeAsync(WebApplicationFactory<Program> factory, string connectionString)
    {
        var tenant = await ShopifyApiHarness.RegisterTenantAsync(factory);
        await ShopifyApiHarness.EnableShopifyAsync(connectionString, tenant.TenantId);
        var connectionId = await ShopifyApiHarness.LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId);
        var advisor = await ShopifyApiHarness.InviteActiveAdvisorAsync(factory, connectionString, tenant);
        var company = await ShopifyApiHarness.CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        var classification = await ShopifyApiHarness.CreateClassificationAsync(tenant.Seeder, tenant.TenantId);
        var tax19 = await ShopifyApiHarness.CreateTaxRateAsync(tenant.Seeder, tenant.TenantId, "IVA 19", 19);
        await ShopifyApiHarness.SetFreightTaxRateAsync(tenant.Seeder, tenant.TenantId, tax19);
        var camiseta = await ShopifyApiHarness.CreateProductAsync(tenant.Seeder, tenant.TenantId, "CAM-001", "Camiseta", 50_000m, tax19);
        var gorra = await ShopifyApiHarness.CreateProductAsync(tenant.Seeder, tenant.TenantId, "GOR-002", "Gorra", 30_000m, tax19);
        return new ShopifyWorld(tenant, connectionId, advisor, company, classification, tax19, camiseta, gorra);
    }

    /// <summary>§6.1: la tienda configurada (asesor, cuenta de cobro COP, clasificación y las claves por defecto), como la deja el
    /// <c>PUT</c> de la tienda.</summary>
    public async Task ConfigureStoreAsync(ShopifyApiHarness.QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = Modules.Shopify.Domain.ShopifyStore.Create(ConnectionId, TenantId, DateTimeOffset.UtcNow);
        store.Configure(
            AdvisorMembershipId,
            new Modules.Shopify.Domain.ShopifyBillingAccount(Company.Id, Company.BankName, Company.AccountNumber, "COP"),
            ClassificationId,
            Modules.Shopify.Domain.ShopifyStore.DefaultDocumentAttributeKey,
            Modules.Shopify.Domain.ShopifyStore.DefaultDocumentTypeAttributeKey,
            Modules.Shopify.Domain.ShopifyStore.DefaultIdentificationTypeCode,
            AdvisorMembershipId,
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<Modules.Shopify.Application.IShopifyStoreRepository>().Add(store);
        await scope.ServiceProvider.GetRequiredService<Modules.Shopify.Application.IShopifyUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
