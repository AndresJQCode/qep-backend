using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Siigo (spec 2026-11-18), copia de <c>ShopifyApiHarness</c>: contenedor compartido, plantilla
/// migrada, una base por prueba y un host que fija sus claves con <c>UseSetting</c>; nunca las hereda de los user-secrets de quien
/// corre las pruebas. Ninguna prueba llama a Siigo: el doble de su API llega con la primera que lo necesita.
/// </summary>
internal static class SiigoApiHarness
{
    /// <summary>El <c>Partner-Id</c> de las pruebas.</summary>
    public const string PartnerId = "qepTest";

    private const string TemplateDatabase = "qep_template_siigo";

    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    private static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

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

    /// <summary><paramref name="Email"/> es el del owner, que es el asesor de los pedidos que crea (y el vendedor en Siigo).</summary>
    internal sealed record RegisteredTenant(Guid TenantId, Guid OwnerUserId, string Email);

    /// <summary>Copia de <c>ShopifyApiHarness.RegisterTenantAsync</c>, en Bogotá.</summary>
    public static async Task<RegisteredTenant> RegisterTenantAsync(WebApplicationFactory<Program> factory)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Siigo Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisteredDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return new RegisteredTenant(registered.TenantId, registered.OwnerUserId, email);
    }

    /// <summary><c>siigo</c> no está en <c>DefaultForNewTenants</c>: se prende por SQL, como <c>pos</c>. También lo vuelve a prender
    /// después de apagarlo.</summary>
    public static Task EnableSiigoAsync(string connectionString, Guid tenantId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'siigo', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId));

    /// <summary>
    /// Una conexión de Siigo creada por los servicios de Integrations (repositorio, cifrado y unidad de trabajo), sin pasar por el
    /// formulario ni el probador: el usuario y la clave son sentinelas que ninguna prueba manda a Siigo.
    /// </summary>
    public static async Task<Guid> LinkSiigoConnectionAsync(WebApplicationFactory<Program> factory, Guid tenantId, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var protector = services.GetRequiredService<ISecretProtector>();
        var now = services.GetRequiredService<IClock>().UtcNow;
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Siigo,
            tenantId,
            name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SiigoFieldKeys.Username] = "api@siigo-test.example.co",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SiigoFieldKeys.AccessKey] = "siigo-access-key-sentinel",
            },
            protector.Protect,
            Guid.CreateVersion7(),
            now);
        services.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        await services.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    /// <summary>Lo que hace falta para sembrar empresas, tasas y productos por las APIs de cada módulo.</summary>
    public static readonly string[] SeedPermissions =
    [
        CatalogPermissions.ProductRead,
        CatalogPermissions.ProductManage,
        CatalogPermissions.TaxRateRead,
        CatalogPermissions.TaxRateManage,
        CompaniesPermissions.CompanyRead,
        CompaniesPermissions.CompanyManage,
        TenancyPermissions.SettingsUpdate,
    ];

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

    internal sealed record CompanyRef(Guid Id, string BankName, string AccountNumber);

    /// <summary>La empresa con su cuenta de cobro en COP (la que el pedido congela en <c>BillingAccount</c>) y, si se pide, tasas de
    /// retención asignadas.</summary>
    public static async Task<CompanyRef> CreateCompanyAsync(
        HttpClient client, Guid tenantId, string name = "Origen Botánico SAS", IReadOnlyList<Guid>? withholdingTaxRateIds = null)
    {
        var accountNumber = $"{Random.Shared.Next(100000000, 999999999)}";
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name,
                bankAccounts = new[] { new { bankName = "Bancolombia", accountNumber, currency = "COP" } },
                taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2",
                country = "CO",
                documentType = "NIT",
                cityId = await AnyCityIdAsync(client),
                phone = "6045551234",
                email = "ventas@origen.example.co",
                address = "Cra 50 # 10-20, Rionegro",
                withholdingTaxRateIds,
            },
            TestContext.Current.CancellationToken);
        await EnsureSuccessAsync(response);
        return new CompanyRef(await ReadIdAsync(response), "Bancolombia", accountNumber);
    }

    /// <summary>Una tasa de IVA (el tipo por defecto) o, con <paramref name="type"/>, de otro tipo (<c>Retefuente</c>).</summary>
    public static async Task<Guid> CreateTaxRateAsync(
        HttpClient client, Guid tenantId, string name, decimal percentage, string? type = null, string? country = null, Guid? cityId = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/tax-rates",
            new { name, percentage, type, country, cityId },
            TestContext.Current.CancellationToken);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    /// <summary>Un producto con una sola variante (el código va en la variante) y precio COP de lista.</summary>
    public static async Task<Guid> CreateProductAsync(
        HttpClient client, Guid tenantId, string code, string name, decimal priceCop, Guid? taxRateId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
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
                                new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1 },
                            },
                            packagingUnits = Array.Empty<int>(),
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    public static async Task<Guid> AnyCityIdAsync(HttpClient client)
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

    /// <summary>Como <c>EnsureSuccessStatusCode</c>, pero el mensaje lleva el cuerpo del problema: sin él, un 422 de una siembra no dice
    /// qué campo rechazó.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.RequestMessage?.RequestUri?.AbsolutePath}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<IdDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body.Id;
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

    /// <param name="siigo">Si viene, es el handler primario del cliente nombrado <c>siigo</c>: ninguna prueba llama a Siigo.</param>
    public sealed class QepApiFactory(string connectionString, FakeSiigoApi? siigo = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            if (siigo is not null)
            {
                // El mismo nombre que SiigoApiClient.HttpClientName, que es interno: el cliente real queda, la red es de mentira.
                builder.ConfigureTestServices(services => services
                    .AddHttpClient("siigo")
                    .ConfigurePrimaryHttpMessageHandler(() => siigo.CreateHandler()));
            }

            builder.UseSetting("ConnectionStrings:QepDatabase", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 40 }.ConnectionString);
            // Fijados, nunca heredados: los user-secrets de quien corre las pruebas pueden prender la semilla o apuntar a otro lado.
            builder.UseSetting("Platform:OperatorTenantId", string.Empty);
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Los workers de Messaging no corren solos durante una prueba de Siigo.
            builder.UseSetting("Messaging:Workers:DeliveryPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:MediaPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:CampaignPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:ScheduledPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:PurgeIntervalHours", "24");
            // La conexión guarda la clave de acceso cifrada: sin llave, LinkSiigoConnectionAsync no puede crearla. Calculada, no
            // escrita, como en IntegrationsApiHarness.
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
            builder.UseSetting("Siigo:PartnerId", PartnerId);
            // El worker de las tandas no corre solo durante una prueba: cada prueba lo corre a mano con DrainAsync.
            builder.UseSetting("Siigo:Batches:PollSeconds", "3600");
        }
    }

    private sealed record RegisteredDto(Guid TenantId, Guid OwnerUserId);

    private sealed record IdDto(Guid Id);
}
