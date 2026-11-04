using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo. Copia el de Suppliers: un
/// tenant con fila (sin ella no hay máscara de módulos ni reloj del tenant) y
/// <c>configureServices</c> en la fábrica.
/// </summary>
internal static class StoreApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string StoreUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/store";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    /// <summary>
    /// Siembra la fila del tenant y sus módulos. Inserta únicamente las claves que faltan, por lo
    /// que se puede llamar dos veces sobre el mismo tenant.
    /// </summary>
    public static async Task SeedTenantAsync(
        QepApiFactory factory,
        string tenantId = TenantId,
        IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId));
        var now = DateTimeOffset.UtcNow;
        var existing = await tenancy.Tenants.FindAsync(
            [id], TestContext.Current.CancellationToken);
        if (existing is null)
        {
            tenancy.Tenants.Add(Tenant.Create(
                id,
                // El slug es único en la base: un segundo tenant necesita el suyo.
                tenantId == TenantId ? "store-api-tests" : $"store-api-{tenantId[^2..]}",
                "Store API Tests",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                now));
            await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({id.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Inserta en <c>customers.customers</c> lo mínimo para que el cliente exista; Store lo
    /// referencia por id suelto, así que las pruebas no necesitan pasar por el módulo Customers.
    /// </summary>
    public static async Task<Guid> SeedCustomerAsync(
        QepApiFactory factory,
        Guid tenantId,
        string name,
        string? phoneE164 = null,
        string? email = null)
    {
        var id = Guid.CreateVersion7();
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, phone_e164, email, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
            VALUES (@id, @tenantId, @name, true, @phone, @email, 'Incomplete', @whatsAppUserId, false, false, 1, now(), now())
            """,
            connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenantId", tenantId);
        insert.Parameters.AddWithValue("name", name);
        // 'Incomplete' porque CK_customers_complete_fields (CustomersDbContext.CompleteFieldsCheck) exige CUC, documento,
        // y país a una ficha 'Complete'; Store sólo necesita nombre, teléfono y correo.
        // Único por llamada: Customers puede exigir un whatsapp_user_id distinto por cliente.
        insert.Parameters.AddWithValue("whatsAppUserId", $"CO.{id:N}");
        insert.Parameters.AddWithValue("phone", (object?)phoneE164 ?? DBNull.Value);
        insert.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Deja al cliente inactivo: «Tomar» no lo ofrece como candidato.</summary>
    public static async Task DeactivateCustomerAsync(QepApiFactory factory, Guid customerId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var update = new NpgsqlCommand(
            "UPDATE customers.customers SET is_active = false WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", customerId);
        await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Siembra una membresía del tenant (con su usuario) con un rol de sistema: <c>admin</c> concede
    /// <c>store.settings.manage</c> y <c>quotations.order.manage</c>; <c>advisor</c> sólo la bandeja. <paramref name="removed"/> la deja quitada.
    /// </summary>
    public static async Task<Guid> SeedMemberAsync(
        QepApiFactory factory,
        string email,
        string? displayName,
        string role,
        bool removed = false,
        string tenantId = TenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var tenant = await tenancy.Tenants.FindAsync([new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId))], ct);
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.New(), email, now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, tenant!.Id, [role], "test", now);
        if (displayName is not null)
        {
            membership.UpdateProfile(displayName, null, now);
        }

        if (removed)
        {
            membership.Remove(tenant, now);
        }

        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }

    /// <summary>
    /// La membresía activa de quien llama (<see cref="SubjectId"/>) en el tenant: la que toma una solicitud y la que
    /// resuelve «Tomados por mí». Sin ella el stub sólo trae permisos y el handler responde 403.
    /// </summary>
    public static async Task<Guid> SeedCallerMembershipAsync(
        QepApiFactory factory, string displayName, string role = "admin", string tenantId = TenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.Parse(SubjectId), $"caller-{tenantId[^2..]}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId)), [role], "test", now);
        membership.UpdateProfile(displayName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }

    /// <summary>La cantidad de filas de <c>audit.entries</c> de una acción sobre un recurso: la auditoría atómica no tiene API de lectura acá.</summary>
    public static async Task<int> CountAuditEntriesAsync(QepApiFactory factory, string action, string resourceId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @resourceId",
            connection);
        count.Parameters.AddWithValue("action", action);
        count.Parameters.AddWithValue("resourceId", resourceId);
        return (int)(long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que el permiso
    // de store se pide explícito: cada 403 queda atribuible al permiso o al tenant.
    public static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Abre base, fábrica y tenant sembrado: el arranque que repiten todas las pruebas.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(
        IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules: modules);
        return (database, factory);
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    internal sealed record CompanyRef(Guid Id, string BankName, string AccountNumber);

    /// <summary>La empresa con su cuenta de cobro en COP, la que configura la tienda. Copia de <c>ShopifyApiHarness</c>.</summary>
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
        return new CompanyRef(await ReadIdAsync(response), "Bancolombia", accountNumber);
    }

    /// <summary>Una empresa con las cuentas que se pidan (banco, número, moneda): la tienda elige una por moneda.</summary>
    public static async Task<CompanyRef> CreateCompanyAsync(
        HttpClient client, Guid tenantId, string name, params (string Bank, string Number, string Currency)[] accounts)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name,
                bankAccounts = accounts.Select(account => new { bankName = account.Bank, accountNumber = account.Number, currency = account.Currency }),
                taxId = $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-2",
                cityId = await AnyCityIdAsync(client),
                phone = "6045551234",
                email = "ventas@origen.example.co",
                address = "Cra 50 # 10-20, Rionegro",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return new CompanyRef(await ReadIdAsync(response), accounts[0].Bank, accounts[0].Number);
    }

    /// <summary>Un producto con precio de lista en cada moneda pedida: sólo así la moneda aparece en <c>currencies-in-use</c>.</summary>
    public static async Task<Guid> CreateProductAsync(
        HttpClient client, Guid tenantId, string code, string name, IReadOnlyDictionary<string, decimal> prices)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                taxRateId = (Guid?)null,
                variants = new[]
                {
                    new
                    {
                        code,
                        pricing = new
                        {
                            prices,
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

    /// <summary>Producto con precio COP de lista y escalas por defecto; el código identifica la variante única.</summary>
    public static async Task<Guid> CreateProductAsync(HttpClient client, Guid tenantId, string code, string name, decimal priceCop, Guid? taxRateId)
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

    private sealed record IdDto(Guid Id);

    /// <param name="configureServices">
    /// Sustituciones de servicios para la prueba; corre después del registro de la aplicación.
    /// </param>
    public sealed class QepApiFactory(
        string connectionString, Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el
            // validador de Notifications falla al arrancar. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos
            // en el tenant de pruebas. El harness no depende del entorno del shell.
            builder.UseSetting("Seed:Enabled", "false");
            // Valores de juguete (nunca reales): el validador de Store exige un secreto de 32+ caracteres fuera de Development.
            builder.UseSetting("Store:Storefront:SharedSecret", "integration-tests-storefront-secret-0123456789");
            builder.UseSetting("Store:Turnstile:SecretKey", "integration-tests-turnstile-secret");

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}

/// <summary>Reloj del tenant movible: las pruebas fijan «hoy» y lo adelantan sin esperar al calendario real.</summary>
internal sealed class MutableTenantClock(DateTimeOffset utcNow) : ITenantClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
}
