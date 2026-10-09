using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// AddQuotationSettings seeds every existing tenant with today's constants, so production
/// behaviour does not change on deploy (spec, "Migration AddQuotationSettings"). The host migrates
/// everything at startup, so the test registers a tenant, walks Quotations back to the previous
/// migration and runs AddQuotationSettings again over a tenant that already exists.
/// </summary>
public sealed class QuotationSettingsMigrationTests
{
    private const string LastMigrationBeforeSettings = "20261008132701_DropTenantWhatsAppSettings";

    [Fact]
    public async Task AddQuotationSettingsSeedsEveryExistingTenantWithTodaysConstants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, "tenancy.settings.read"]);
        using var _ = client;
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var migrator = context.GetService<IMigrator>();
        var settingsMigration = context.Database.GetMigrations()
            .Single(id => id.EndsWith("_AddQuotationSettings", StringComparison.Ordinal));

        await migrator.MigrateAsync(LastMigrationBeforeSettings, TestContext.Current.CancellationToken);
        await migrator.MigrateAsync(settingsMigration, TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT s.minimum_units || '|' || string_agg(t.currency || '=' || t.amount::text, ',' ORDER BY t.currency) " +
            "FROM quotations.tenant_quotation_settings s " +
            "JOIN quotations.tenant_minimum_totals t ON t.tenant_id = s.tenant_id " +
            "WHERE s.tenant_id = @tenantId GROUP BY s.minimum_units",
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId);

        Assert.Equal("6|COP=500000.00,USD=200.00",
            (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        // And the gate of that tenant behaves as before the deploy: two COP units of 300.000 with
        // their 10% (540.000) pass by the 500.000 total, exactly as the old constant did.
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(
            client, tenantId,
            baseCop: 300_000m,
            scales: [new { fromUnit = 1, toUnit = 999_999, discount = 10m, restriction = "multiple", multiple = 1 }]);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/items",
            new AddQuotationItemRequest(productId, 2m),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<QuotationResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(10m, Assert.Single(updated!.Items).DiscountPercentage);
        Assert.True(updated.MinimumPurchase.Met);
        Assert.Equal((6m, (decimal?)500_000m), (updated.MinimumPurchase.MinimumUnits, updated.MinimumPurchase.MinimumTotal));
    }
}
