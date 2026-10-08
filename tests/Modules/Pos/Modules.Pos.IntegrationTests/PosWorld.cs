using System.Net.Http.Json;
using Modules.Pos.Application;
using Testcontainers.PostgreSql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>El tenant del ejemplo trabajado del spec, listo para vender.</summary>
internal sealed record PosWorld(
    PosTenant Tenant,
    HttpClient Admin,
    CompanyRef Company,
    Guid Shampoo,
    Guid Avena,
    Guid Jabon,
    Guid Tax19,
    Guid Tax5)
{
    public string Url => PosUrl(Tenant.TenantId);

    public static async Task<PosWorld> ArrangeAsync(QepApiFactory factory, PostgreSqlContainer database)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnablePosAsync(database, tenant.TenantId);
        var company = await CreateCompanyAsync(tenant.Seeder, tenant.TenantId);
        var tax19 = await CreateTaxRateAsync(tenant.Seeder, tenant.TenantId, "IVA 19", 19);
        var tax5 = await CreateTaxRateAsync(tenant.Seeder, tenant.TenantId, "IVA 5", 5);
        var shampoo = await CreateProductAsync(tenant.Seeder, tenant.TenantId, "SH-400", "Shampoo 400 ml", 11_900m, tax19);
        var avena = await CreateProductAsync(tenant.Seeder, tenant.TenantId, "AV-01", "Avena granel (kg)", 5_000m, null);
        var jabon = await CreateProductAsync(tenant.Seeder, tenant.TenantId, "JB-03", "Jabón", 2_990m, tax5);
        // El dueño del tenant es miembro activo (admin): el handler lo resuelve a su membresía.
        var admin = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, AdminPosPermissions);
        return new PosWorld(tenant, admin, company, shampoo, avena, jabon, tax19, tax5);
    }

    public object[] WorkedExampleLines() =>
    [
        new { productId = Shampoo, quantity = 2m, discountPercentage = 10m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 19 },
        new { productId = Avena, quantity = 1.5m, discountPercentage = 0m, expectedUnitPrice = 5_000m, expectedTaxPercentage = 0 },
        new { productId = Jabon, quantity = 3m, discountPercentage = 0m, expectedUnitPrice = 2_990m, expectedTaxPercentage = 5 },
    ];

    public object[] PlainLine(decimal quantity = 1m) =>
    [
        new { productId = Shampoo, quantity, discountPercentage = 0m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 19 },
    ];

    public static object[] SplitPayments() =>
    [
        new { method = "Card", amount = 20_000m, reference = "1234" },
        new { method = "Cash", tendered = 20_000m },
    ];

    public static object[] CashPayment(decimal tendered) => [new { method = "Cash", tendered }];

    public static object SaleBody(Guid id, Guid sessionId, object[] lines, object[] payments) =>
        new { id, cashSessionId = sessionId, lines, payments };

    public async Task<PosOpenSessionResponse> OpenSessionAsync(HttpClient client, decimal openingFloat = 100_000m)
    {
        var response = await client.PostAsJsonAsync(
            $"{Url}/sessions", new { companyId = (Guid?)null, openingFloat }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<PosOpenSessionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        return session;
    }
}
