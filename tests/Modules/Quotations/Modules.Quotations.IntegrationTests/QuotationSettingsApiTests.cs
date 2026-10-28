using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Npgsql;
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
        using var ownedClient = client;

        var response = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits, minimumTotals = new Dictionary<string, decimal> { [code] = amount } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    // Without the map the PUT would otherwise read as "delete every total": it is a 422 instead.
    [Fact]
    public async Task APutWithoutTheTotalsMapIs422OnMinimumTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var ownedClient = client;

        var response = await client.PutAsJsonAsync(
            SettingsUrl(tenantId), new { minimumUnits = 6 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("MinimumTotals", out _));
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

    // Review round 1, deterministic: another save for a tenant without a row is mid-transaction
    // (parent and children written, not committed) when this PUT arrives. The PUT must wait for it
    // and then replace its whole document — never a 500 on the primary key, and never a merge of
    // the two bodies (the other save's COP/USD must not survive next to this PUT's EUR).
    [Fact]
    public async Task APutRacingAnotherFirstSaveWaitsAndReplacesItsWholeDocument()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var other = new NpgsqlConnection(database.GetConnectionString());
        await other.OpenAsync(cancellationToken);
        await using var otherSave = await other.BeginTransactionAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO quotations.tenant_quotation_settings (tenant_id, minimum_units) VALUES (@tenantId, 9); " +
            "INSERT INTO quotations.tenant_minimum_totals (tenant_id, currency, amount) VALUES " +
            "(@tenantId, 'COP', 1), (@tenantId, 'USD', 2);",
            other,
            otherSave))
        {
            insert.Parameters.AddWithValue("tenantId", tenantId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var put = client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits = 7, minimumTotals = new Dictionary<string, decimal> { ["EUR"] = 3m } },
            cancellationToken);
        await WaitUntilABackendWaitsOnALockAsync(database.GetConnectionString(), put, cancellationToken);
        await otherSave.CommitAsync(cancellationToken);

        var response = await put;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = await client.GetFromJsonAsync<SettingsPayload>(SettingsUrl(tenantId), cancellationToken);
        Assert.Equal(7, settings!.MinimumUnits);
        Assert.Equal(new Dictionary<string, decimal> { ["EUR"] = 3m }, settings.MinimumTotals);
    }

    // Review round 1, free-running: two first saves at once. Serialized (both 200) or one 412,
    // never a 500; and what is stored is exactly one of the two bodies, never a merge.
    [Fact]
    public async Task TwoConcurrentFirstPutsNeverFailWithA500NorMergeTheirBodies()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = new Dictionary<string, decimal> { ["COP"] = 1m, ["USD"] = 2m };
        var second = new Dictionary<string, decimal> { ["EUR"] = 3m };

        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(SettingsUrl(tenantId), new { minimumUnits = 9, minimumTotals = first }, cancellationToken),
            client.PutAsJsonAsync(SettingsUrl(tenantId), new { minimumUnits = 7, minimumTotals = second }, cancellationToken));

        Assert.All(responses, response => Assert.Contains(
            response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed }));
        var settings = await client.GetFromJsonAsync<SettingsPayload>(SettingsUrl(tenantId), cancellationToken);
        Assert.True(
            (settings!.MinimumUnits == 9 && DictionaryEquals(first, settings.MinimumTotals))
            || (settings.MinimumUnits == 7 && DictionaryEquals(second, settings.MinimumTotals)),
            $"Stored {settings.MinimumUnits} / {string.Join(",", settings.MinimumTotals)} is neither body.");
    }

    // Same answer as every other module-gated endpoint (TenantModulesQuotationsApiTests): 403
    // tenancy.module_not_enabled, for the read and for the write.
    [Fact]
    public async Task WithTheQuotationsModuleOffGetAndPutAreModuleNotEnabled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, SettingsRead, SettingsUpdate);
        using var _ = client;
        var cancellationToken = TestContext.Current.CancellationToken;
        await DisableModuleAsync(factory, tenantId, Modules.Tenancy.Domain.TenantModuleKeys.Quotations);

        var get = await client.GetAsync(SettingsUrl(tenantId), cancellationToken);
        var put = await client.PutAsJsonAsync(
            SettingsUrl(tenantId),
            new { minimumUnits = 6, minimumTotals = new Dictionary<string, decimal>() },
            cancellationToken);

        foreach (var response in new[] { get, put })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("tenancy.module_not_enabled", problem.RootElement.GetProperty("code").GetString());
        }
    }

    private static bool DictionaryEquals(
        Dictionary<string, decimal> expected, Dictionary<string, decimal> actual) =>
        expected.Count == actual.Count
        && expected.All(entry => actual.TryGetValue(entry.Key, out var amount) && amount == entry.Value);

    // The PUT is in flight once some backend waits on a lock; if it finishes first instead, the
    // race did not happen and the test says so rather than passing for the wrong reason.
    private static async Task WaitUntilABackendWaitsOnALockAsync(
        string connectionString, Task pending, CancellationToken cancellationToken)
    {
        await using var probe = new NpgsqlConnection(connectionString);
        await probe.OpenAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(pending.IsCompleted, "The PUT finished without waiting for the other save.");
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock'", probe);
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail("The PUT never blocked on the other save.");
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
