using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

public sealed class QuotationSettingsApiTests
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";

    private static string SettingsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/quotations/settings";

    [Fact]
    public async Task ATenantWithoutStoredSettingsReadsTheDefaults()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        var settings = await client.GetFromJsonAsync<SettingsPayload>(SettingsUrl(tenantId), TestContext.Current.CancellationToken);

        Assert.Equal(6, settings!.MinimumUnits);
        Assert.Equal(new Dictionary<string, decimal> { ["COP"] = 500_000m, ["USD"] = 200m }, settings.MinimumTotals);
    }

    // A code absent from the body deletes its row (spec): USD and COP go, EUR comes.
    [Fact]
    public async Task PutReplacesTheWholeDocument()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var put = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits = 10, minimumTotals = new Dictionary<string, decimal> { ["eur"] = 150m } },
            TestContext.Current.CancellationToken);
        put.EnsureSuccessStatusCode();
        var saved = await put.Content.ReadFromJsonAsync<SettingsPayload>(TestContext.Current.CancellationToken);

        var settings = await client.GetFromJsonAsync<SettingsPayload>(SettingsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.Equal(10, settings!.MinimumUnits);
        Assert.Equal(new Dictionary<string, decimal> { ["EUR"] = 150m }, settings.MinimumTotals);
        // The PUT answers with the saved document, which the form repaints.
        Assert.Equal(settings.MinimumTotals, saved!.MinimumTotals);
    }

    // The keys are a contract: the frontend reads errors["MinimumTotals.<CODE>"] with a regex to
    // mark the row, and errors.MinimumUnits for the units field.
    [Theory]
    [InlineData(0, "COP", 1, "MinimumUnits")]
    [InlineData(6, "XYZ", 1, "MinimumTotals.XYZ")]
    [InlineData(6, "COP", -1, "MinimumTotals.COP")]
    public async Task InvalidValuesAre422OnTheirField(int minimumUnits, string code, int amount, string field)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var response = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits, minimumTotals = new Dictionary<string, decimal> { [code] = amount } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out var fieldErrors));
    }

    // Without the map the PUT would otherwise read as "delete every total": it is a 422 instead.
    [Fact]
    public async Task APutWithoutTheTotalsMapIs422OnMinimumTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;

        var response = await client.PutAsJsonAsync(
            SettingsUrl(tenantId), new { minimumUnits = 6 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("MinimumTotals", out var fieldErrors));
    }

    [Fact]
    public async Task PutWithoutTheSettingsUpdatePermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead);
        using var _ = client;

        var response = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits = 6, minimumTotals = new Dictionary<string, decimal>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Review Focus 3, end to end: COP has a minimum, EUR has none. Two units of an EUR product
    // worth 1.000.000 each keep no scale discount — only six units would open it.
    [Fact]
    public async Task AnEurQuotationWithoutAConfiguredTotalOnlyGetsTheDiscountByUnits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, SettingsRead, SettingsUpdate]);
        using var _ = client;
        await PutSettingsAsync(client, tenantId, new Dictionary<string, decimal> { ["COP"] = 500_000m });

        var updated = await AddTwoEurUnitsAsync(client, tenantId);

        Assert.Equal(0m, Assert.Single(updated.Items).DiscountPercentage);
        Assert.False(updated.MinimumPurchase.Met);
        Assert.Equal("EUR", updated.MinimumPurchase.Currency);
        Assert.Null(updated.MinimumPurchase.MinimumTotal);
        Assert.Null(updated.MinimumPurchase.MissingTotal);
        Assert.Equal(4m, updated.MinimumPurchase.MissingUnits);
    }

    // The other half: once the tenant configures an EUR minimum, the same quotation passes by its
    // EUR total (2 x 1.000.000 with 10% = 1.800.000 >= 1.500.000).
    [Fact]
    public async Task AnEurQuotationWithAConfiguredTotalPassesByTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, SettingsRead, SettingsUpdate]);
        using var _ = client;
        await PutSettingsAsync(client, tenantId, new Dictionary<string, decimal> { ["EUR"] = 1_500_000m });

        var updated = await AddTwoEurUnitsAsync(client, tenantId);

        Assert.Equal(10m, Assert.Single(updated.Items).DiscountPercentage);
        Assert.True(updated.MinimumPurchase.Met);
        Assert.Equal("EUR", updated.MinimumPurchase.Currency);
        Assert.Equal(1_500_000m, updated.MinimumPurchase.MinimumTotal);
        Assert.Equal(0m, updated.MinimumPurchase.MissingTotal);
    }

    private static async Task PutSettingsAsync(
        HttpClient client, Guid tenantId, Dictionary<string, decimal> minimumTotals) =>
        (await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits = 6, minimumTotals },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

    private static async Task<QuotationResponse> AddTwoEurUnitsAsync(HttpClient client, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId,
            scales: [new { fromUnit = 1, toUnit = 999_999, discount = 10m, restriction = "multiple", multiple = 1 }],
            prices: new Dictionary<string, decimal> { ["EUR"] = 1_000_000m });
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId, currency: "EUR");
        var quotation = await CreateQuotationAsync(
            client, tenantId, clientId,
            billingAccount: new QuotationBillingAccountRequest(
                billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 2m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken))!;
    }

    private sealed record SettingsPayload(int MinimumUnits, Dictionary<string, decimal> MinimumTotals);
}
