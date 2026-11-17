using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Application;
using Modules.Integrations.Domain;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4, plan Tarea 5: los adaptadores que componen el asistente, contra el esquema migrado. Cada
/// prueba levanta su host: varias sustituyen el contexto de ejecución.
/// </summary>
public sealed class ShopifySetupAdaptersTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string StoredProfile =
        """{"email":"dueno@mitienda.co","currencyCode":"COP","taxesIncluded":true,"billingAddress":{"company":"Mi Tienda SAS","city":"Medellín","provinceCode":"ANT","countryCode":"CO","phone":"+573001112233"}}""";

    private static Task SetShopProfileAsync(string connectionString, Guid connectionId, string profile) =>
        ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET fields = fields || jsonb_build_object('shopProfile', @profile::text) WHERE id = @id",
            ("profile", profile),
            ("id", connectionId));

    [Fact]
    public async Task TheDirectoryReadsTheStoredProfile()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, Guid.CreateVersion7());
        await SetShopProfileAsync(connectionString, connectionId, StoredProfile);

        using var scope = factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IShopifyConnectionDirectory>()
            .FindAsync(tenant.TenantId, connectionId, Ct);

        Assert.NotNull(connection);
        Assert.NotNull(connection.Profile);
        Assert.Equal("COP", connection.Profile.CurrencyCode);
        Assert.Equal("Medellín", connection.Profile.BillingAddress?.City);
        Assert.Equal("ANT", connection.Profile.BillingAddress?.ProvinceCode);
        // El perfil lleva correo y teléfono: el ToString de la referencia (el que cae en un log) no lo trae.
        Assert.DoesNotContain("dueno@mitienda.co", connection.ToString(), StringComparison.Ordinal);
    }

    // Review Focus 5: un perfil viejo o roto se lee como que no hay perfil, nunca como un 500.
    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    public async Task ABrokenStoredProfileIsNull(string profile)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, Guid.CreateVersion7());
        await SetShopProfileAsync(connectionString, connectionId, profile);

        using var scope = factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IShopifyConnectionDirectory>()
            .FindAsync(tenant.TenantId, connectionId, Ct);

        Assert.NotNull(connection);
        Assert.Null(connection.Profile);
    }

    // Plan D11: sólo las filas Waiting, por motivo; la muestra son hasta 5 SKU distintos de product_unmatched.
    [Fact]
    public async Task WaitingIsGroupedByReasonWithFiveSkus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        string[] skus = ["SKU-1", "SKU-2", "SKU-3", "SKU-4", "SKU-5", "SKU-6", "SKU-1"];
        var rows = new List<ShopifyOrderMirror>();
        for (var index = 0; index < skus.Length; index++)
        {
            rows.Add(Waiting(tenantId, connectionId, 1_000 + index, ShopifyWaitReasons.ProductUnmatched, $$"""{"skus":["{{skus[index]}}"]}"""));
        }

        rows.Add(Waiting(tenantId, connectionId, 2_000, ShopifyWaitReasons.AdvisorInactive, null));
        rows.Add(Waiting(tenantId, connectionId, 2_001, ShopifyWaitReasons.AdvisorInactive, null));
        await SaveAsync(factory, rows);

        using var scope = factory.Services.CreateScope();
        var groups = await scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>()
            .SummarizeWaitingAsync(tenantId, connectionId, shopifyOrderIds: null, Ct);

        Assert.Equal(
            [(ShopifyWaitReasons.ProductUnmatched, 7), (ShopifyWaitReasons.AdvisorInactive, 2)],
            groups.Select(group => (group.Reason, group.Count)));
        Assert.Equal(5, groups[0].Sample.Count);
        Assert.Equal(groups[0].Sample.Count, groups[0].Sample.Distinct(StringComparer.Ordinal).Count());
        Assert.All(groups[0].Sample, sku => Assert.Contains(sku, skus));
        Assert.Empty(groups[1].Sample);

        // Con ids, sólo esos pedidos.
        var only = await scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>()
            .SummarizeWaitingAsync(tenantId, connectionId, [2_000L], Ct);
        Assert.Equal([(ShopifyWaitReasons.AdvisorInactive, 1)], only.Select(group => (group.Reason, group.Count)));
    }

    [Fact]
    public async Task WaitingOfAnotherConnectionIsNotCounted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await SaveAsync(
            factory,
            [
                Waiting(tenantId, Guid.CreateVersion7(), 1, ShopifyWaitReasons.ProductUnmatched, """{"skus":["X"]}"""),
                Waiting(Guid.CreateVersion7(), connectionId, 2, ShopifyWaitReasons.ProductUnmatched, """{"skus":["Y"]}"""),
            ]);

        using var scope = factory.Services.CreateScope();
        var groups = await scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>()
            .SummarizeWaitingAsync(tenantId, connectionId, shopifyOrderIds: null, Ct);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task ListActiveCompaniesSkipsInactiveOnes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var active = await CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        var inactive = await CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        await ExecuteAsync(connectionString, "UPDATE companies.companies SET is_active = false WHERE id = @id", ("id", inactive.Id));

        using var scope = factory.Services.CreateScope();
        var companies = scope.ServiceProvider.GetRequiredService<IShopifySetupCompanies>();
        var listed = await companies.ListActiveAsync(tenant.TenantId, Ct);

        var company = Assert.Single(listed);
        Assert.Equal(active.Id, company.Id);
        Assert.Equal("NIT", company.DocumentType);
        Assert.Equal("CO", company.Country);
        Assert.Equal([new ShopifySetupBankAccount(active.BankName, active.AccountNumber, "COP")], company.BankAccounts);
        Assert.NotNull(await companies.FindActiveAsync(tenant.TenantId, active.Id, Ct));
        Assert.Null(await companies.FindActiveAsync(tenant.TenantId, inactive.Id, Ct));
        Assert.Null(await companies.FindActiveAsync(Guid.CreateVersion7(), active.Id, Ct));
    }

    // Plan D5: la creación pasa por el caso de uso de Companies (autorización, validador, ciudad y siembra de tasas).
    [Fact]
    public async Task CreateRunsTheCompaniesUseCase()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var cityId = await AnyCityIdAsync(tenant.Seeder);
        using var host = WithContext(factory, tenant.TenantId, tenant.OwnerUserId, CompaniesPermissions.CompanyManage);

        ShopifySetupCompanyRef withoutVat;
        ShopifySetupCompanyRef withVat;
        using (var scope = host.Services.CreateScope())
        {
            var companies = scope.ServiceProvider.GetRequiredService<IShopifySetupCompanies>();
            withoutVat = await companies.CreateAsync(Draft(tenant.TenantId, cityId, chargesVat: false, account: null), Ct);
        }

        using (var scope = host.Services.CreateScope())
        {
            var companies = scope.ServiceProvider.GetRequiredService<IShopifySetupCompanies>();
            withVat = await companies.CreateAsync(
                Draft(tenant.TenantId, cityId, chargesVat: true, account: new ShopifySetupBankAccount("Bancolombia", "123456789", "COP")), Ct);
        }

        Assert.False(await ScalarAsync<bool>(
            connectionString, "SELECT charges_vat FROM companies.companies WHERE id = @id AND tenant_id = @tenantId",
            ("id", withoutVat.Id), ("tenantId", tenant.TenantId)));
        Assert.False(withoutVat.ChargesVat);
        Assert.Empty(withoutVat.BankAccounts);
        Assert.True(withVat.ChargesVat);
        Assert.Equal([new ShopifySetupBankAccount("Bancolombia", "123456789", "COP")], withVat.BankAccounts);
        // Spec reglas de empresa §5.3: una empresa colombiana que cobra IVA siembra las tasas por defecto.
        Assert.True(await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM catalog.tax_rates WHERE tenant_id = @tenantId", ("tenantId", tenant.TenantId)) > 0);
    }

    [Fact]
    public async Task CreateWithoutCompanyManageIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var cityId = await AnyCityIdAsync(tenant.Seeder);
        using var host = WithContext(factory, tenant.TenantId, tenant.OwnerUserId, CompaniesPermissions.CompanyRead);

        using var scope = host.Services.CreateScope();
        var companies = scope.ServiceProvider.GetRequiredService<IShopifySetupCompanies>();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            companies.CreateAsync(Draft(tenant.TenantId, cityId, chargesVat: true, account: null), Ct));
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM companies.companies WHERE tenant_id = @tenantId", ("tenantId", tenant.TenantId)));
    }

    [Fact]
    public async Task InactiveClassificationsAreNotListed()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var active = await CreateClassificationAsync(tenant.Seeder, tenant.TenantId);
        var other = await tenant.Seeder.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/customers/classifications", new { name = "Mayoristas", prefix = "MAY" }, Ct);
        other.EnsureSuccessStatusCode();
        await ExecuteAsync(
            connectionString,
            "UPDATE customers.client_classifications SET is_active = false WHERE tenant_id = @tenantId AND id <> @id",
            ("tenantId", tenant.TenantId),
            ("id", active));

        using var scope = factory.Services.CreateScope();
        var listed = await scope.ServiceProvider.GetRequiredService<IShopifyStoreReferences>()
            .ListClassificationsAsync(tenant.TenantId, Ct);

        Assert.Equal([new ShopifyNamedRef(active, "Tienda en línea")], listed);
    }

    [Fact]
    public async Task ActiveMembersAreListedByName()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var advisor = await InviteActiveAdvisorAsync(factory, connectionString, tenant);

        using var scope = factory.Services.CreateScope();
        var members = await scope.ServiceProvider.GetRequiredService<IShopifyMemberDirectory>().ListActiveAsync(tenant.TenantId, Ct);

        Assert.Contains(members, member => member.MembershipId == advisor && member.Name == "Asesora Shopify");
        Assert.Equal(members.OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase).Select(member => member.MembershipId), members.Select(member => member.MembershipId));
    }

    private static ShopifySetupCompanyDraft Draft(Guid tenantId, Guid cityId, bool chargesVat, ShopifySetupBankAccount? account) =>
        new(
            tenantId,
            "Mi Tienda SAS",
            $"901.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}-1",
            "NIT",
            "CO",
            cityId,
            null,
            "Calle 10 # 43-12",
            "6045551234",
            "ventas@mitienda.example.co",
            chargesVat,
            account);

    private static ShopifyOrderMirror Waiting(Guid tenantId, Guid connectionId, long shopifyOrderId, string reason, string? detail)
    {
        var row = ShopifyOrderMirror.Create(
            tenantId,
            connectionId,
            shopifyOrderId,
            """{"name":"#1001","lines":[]}""",
            Now,
            new ShopifyOrderHeader("#1001", null, Now.AddMinutes(-5), null, "pending", "COP", 1_000m, false));
        row.MarkWaiting(reason, detail, Now);
        return row;
    }

    private static async Task SaveAsync(WebApplicationFactory<Program> factory, IEnumerable<ShopifyOrderMirror> rows)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
        db.Orders.AddRange(rows);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
    }

    /// <summary>El host con un contexto de ejecución fijo: el caso de uso de Companies autoriza con él.</summary>
    private static WebApplicationFactory<Program> WithContext(
        WebApplicationFactory<Program> factory, Guid tenantId, Guid subjectId, params string[] permissions) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IExecutionContext>(_ => new FixedContext(tenantId, subjectId, permissions))));

    private sealed class FixedContext(Guid tenantId, Guid subjectId, string[] permissions) : IExecutionContext
    {
        public Guid SubjectId => subjectId;

        public TenantId TenantId => new(tenantId);

        public bool HasPermission(string permission) => permissions.Contains(permission);
    }
}
