using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wholesale.Application;
using Testcontainers.PostgreSql;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>Un tenant sembrado para una prueba: quien llama (<c>SubjectId</c>) y su membresía activa <c>admin</c>.</summary>
internal sealed record WholesaleTestTenant(Guid TenantId, Guid SubjectId, Guid MemberId);

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo (base y fábrica copiadas de <c>PurchasesApiHarness</c>), con
/// un tenant nuevo por prueba (<see cref="NewTenantAsync"/>) para que varias compartan host (<see cref="SharedWholesaleHost"/>).
/// </summary>
internal static class WholesaleApiHarness
{
    public const string MemberName = "Ana Mayorista";

    public const string DistributorInvitedEvent = "wholesale.distributor-invited.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string PriceListsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/wholesale/price-lists";

    public static string DistributorsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/wholesale/distributors";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(Ct);
        return database;
    }

    /// <summary>
    /// Siembra un tenant nuevo con sus módulos (todos por defecto), el usuario que llama y su membresía activa <c>admin</c> con
    /// nombre «Ana Mayorista», el owner del tenant.
    /// </summary>
    public static async Task<WholesaleTestTenant> NewTenantAsync(
        QepApiFactory factory, IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenantId = new Modules.Tenancy.Domain.TenantId(Guid.CreateVersion7());
        var subjectId = Guid.CreateVersion7();
        var membershipId = MembershipId.New();
        var now = DateTimeOffset.UtcNow;

        tenancy.Tenants.Add(Tenant.Create(
            tenantId,
            $"wholesale-{tenantId.Value:N}",
            "Mayorista API Tests",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            membershipId,
            now));
        await tenancy.SaveChangesAsync(Ct);

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({tenantId.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                Ct);
        }

        var user = User.CreateInvited(UserId.Parse(subjectId.ToString()), $"mayorista-{subjectId:N}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(membershipId, user.Id.Value, tenantId, ["admin"], "test", now);
        membership.UpdateProfile(MemberName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(Ct);
        await tenancy.SaveChangesAsync(Ct);

        return new WholesaleTestTenant(tenantId.Value, subjectId, membership.Id.Value);
    }

    public static HttpClient CreateClient(QepApiFactory factory, Guid subjectId, Guid tenantId, params string[] permissions)
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

    /// <summary>Gestiona listas y distribuidores y, para sembrar, empresas y catálogo.</summary>
    public static HttpClient CreateManager(QepApiFactory factory, WholesaleTestTenant tenant) =>
        CreateClient(
            factory,
            tenant.SubjectId,
            tenant.TenantId,
            WholesalePermissions.PriceListManage,
            WholesalePermissions.DistributorRead,
            WholesalePermissions.DistributorManage,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage,
            CatalogPermissions.ProductRead,
            CatalogPermissions.ProductManage);

    public static HttpClient CreateReader(QepApiFactory factory, WholesaleTestTenant tenant) =>
        CreateClient(factory, tenant.SubjectId, tenant.TenantId, WholesalePermissions.DistributorRead);

    /// <summary>Una empresa facturadora con NIT en Medellín (o la primera ciudad sembrada si no está).</summary>
    public static async Task<Guid> CreateCompanyAsync(HttpClient client, Guid tenantId, string name, string taxId = "900111222")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name,
                bankAccounts = new[] { new { bankName = "Bancolombia", accountNumber = "123456789", currency = "COP" } },
                taxId,
                country = "CO",
                documentType = "NIT",
                phone = "3001234567",
                email = "ventas@mayorista.co",
                address = "Calle 10 # 40-20",
                cityId = await MedellinAsync(client),
            },
            Ct);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    public static async Task<Guid> CreateCategoryAsync(HttpClient client, Guid tenantId, string name, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories", new { name, parentId }, Ct);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    /// <summary>
    /// Un producto con una sola variante (copia de <c>PurchasesApiHarness.CreateVariantAsync</c>); devuelve el id de la variante.
    /// <paramref name="priceCop"/> nulo deja la variante sin precio en COP (con uno en USD).
    /// </summary>
    public static async Task<Guid> CreateVariantAsync(
        HttpClient client, Guid tenantId, string code, string name, decimal? priceCop, Guid? categoryId = null)
    {
        var prices = priceCop is { } cop
            ? new Dictionary<string, decimal> { ["COP"] = cop }
            : new Dictionary<string, decimal> { ["USD"] = 10m };
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                categoryId,
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
            Ct);
        await EnsureSuccessAsync(response);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    public static async Task<PriceListDetailDto> CreatePriceListAsync(
        HttpClient client, Guid tenantId, string name, decimal discountPercent, string visibility = "AllActive", params Guid[] categoryIds)
    {
        var response = await client.PostAsJsonAsync(
            PriceListsUrl(tenantId), new { name, discountPercent, visibility, categoryIds }, Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
    }

    /// <summary>Invita y devuelve el detalle y el token plano, leído del outbox (es el único lugar donde existe).</summary>
    public static async Task<(DistributorDetailDto Distributor, string Token)> InviteAsync(
        QepApiFactory factory,
        HttpClient client,
        WholesaleTestTenant tenant,
        Guid priceListId,
        Guid billingCompanyId,
        string email = "ana@distri.co",
        string identificationNumber = "900123456")
    {
        var response = await client.PostAsJsonAsync(
            DistributorsUrl(tenant.TenantId),
            new
            {
                email,
                businessName = "Distribuciones Norte SAS",
                identificationNumber,
                contactName = "Ana Pérez",
                priceListId,
                advisorId = tenant.MemberId,
                billingCompanyId,
            },
            Ct);
        await EnsureSuccessAsync(response);
        var distributor = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        return (distributor, await LatestTokenAsync(factory, distributor.Id));
    }

    /// <summary>El token del último evento de invitación de ese distribuidor (un reenvío publica otro).</summary>
    public static async Task<string> LatestTokenAsync(QepApiFactory factory, Guid distributorId)
    {
        var payloads = await RowsAsync<string>(
            factory,
            $"""
            SELECT payload::text AS "Value" FROM platform.outbox_messages
            WHERE event_name = {DistributorInvitedEvent} AND payload ->> 'distributorId' = {distributorId.ToString()}
            ORDER BY occurred_at DESC, id DESC
            """);
        using var document = JsonDocument.Parse(payloads[0]);
        return document.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>El <c>If-Match</c> de las escrituras sobre un recurso existente, con la forma fuerte <c>"7"</c>.</summary>
    public static HttpRequestMessage WithIfMatch(HttpRequestMessage request, long version)
    {
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return request;
    }

    public static async Task<IReadOnlyList<T>> RowsAsync<T>(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Database.SqlQuery<T>(sql).ToListAsync(Ct);
    }

    public static async Task ExecuteAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await tenancy.Database.ExecuteSqlInterpolatedAsync(sql, Ct);
    }

    public static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(Ct);

    private static async Task<Guid> MedellinAsync(HttpClient client)
    {
        Guid? first = null;
        var departments = await client.GetFromJsonAsync<List<GeographyRow>>("/api/v1/departments", Ct) ?? [];
        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyRow>>($"/api/v1/cities?departmentId={department.Id}", Ct) ?? [];
            first ??= cities.FirstOrDefault()?.Id;
            var medellin = cities.FirstOrDefault(city => city.Name.StartsWith("Medell", StringComparison.OrdinalIgnoreCase));
            if (medellin is not null)
            {
                return medellin.Id;
            }
        }

        return first ?? throw new InvalidOperationException("No seeded DIVIPOLA city.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {await BodyAsync(response)}");
        }
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record GeographyRow(Guid Id, string Name);

    /// <param name="configureServices">Sustituciones de servicios para la prueba; corre después del registro de la aplicación.</param>
    public sealed class QepApiFactory(string connectionString, Action<IServiceCollection>? configureServices = null)
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el validador de Notifications falla
            // al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos en la base de pruebas.
            builder.UseSetting("Seed:Enabled", "false");

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
