using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// OptionalBillingAccountAndChargesVat (spec 2026-11-17, R1, R3, R5) contra documentos guardados antes de la migración.
/// Base propia por prueba (toca el esquema): los documentos se arman por la API con el esquema de hoy, Quotations baja a la
/// migración anterior —que quita las columnas nuevas— y vuelve a subir, así las filas quedan exactamente como las encuentra
/// la migración en producción. Review Focus 1 (un <c>false</c> que se pierde al insertar) lo pina la última.
/// </summary>
public sealed class OptionalBillingAccountMigrationTests
{
    private const string MigrationSuffix = "_OptionalBillingAccountAndChargesVat";

    [Fact]
    public async Task ExistingQuotationsWithAccountReadChargesVatTrue()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedThroughTheApiAsync(database.GetConnectionString());

        await RemigrateAsync(database.GetConnectionString());

        Assert.True(await ScalarAsync<bool>(
            database.GetConnectionString(),
            $"SELECT billing_company_charges_vat FROM quotations.quotations WHERE id = '{seeded.QuotationId}'"));
        await using var context = NewContext(database.GetConnectionString());
        var quotation = await context.Set<Quotation>().SingleAsync(
            row => row.Id == new QuotationId(seeded.QuotationId), TestContext.Current.CancellationToken);
        Assert.True(quotation.ChargesVat);
    }

    [Fact]
    public async Task ExistingOrdersChargeVatAndKeepTheirAccount()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedThroughTheApiAsync(database.GetConnectionString());

        await RemigrateAsync(database.GetConnectionString());

        Assert.Equal(
            "true|Bancolombia",
            await ScalarAsync<string>(
                database.GetConnectionString(),
                "SELECT billing_company_charges_vat::text || '|' || billing_bank_name FROM quotations.orders " +
                $"WHERE id = '{seeded.OrderId}'"));
    }

    [Fact]
    public async Task LegacyLinesHaveNoProductRate()
    {
        await using var database = await StartDatabaseAsync();
        var seeded = await SeedThroughTheApiAsync(database.GetConnectionString());

        await RemigrateAsync(database.GetConnectionString());

        // Sin relleno (R1): la tasa del producto queda en null y la efectiva intacta.
        Assert.Equal(
            "null/19",
            await ScalarAsync<string>(
                database.GetConnectionString(),
                "SELECT string_agg(coalesce(product_tax_percentage::text, 'null') || '/' || tax_percentage::text, ',') " +
                $"FROM quotations.quotation_items WHERE quotation_id = '{seeded.QuotationId}'"));
        Assert.Equal(
            "null/19",
            await ScalarAsync<string>(
                database.GetConnectionString(),
                "SELECT string_agg(coalesce(product_tax_percentage::text, 'null') || '/' || tax_percentage::text, ',') " +
                $"FROM quotations.order_items WHERE order_id = '{seeded.OrderId}'"));
    }

    // Review Focus 1: con el esquema de hoy, el false de una empresa sin IVA llega a la base y vuelve en false.
    [Fact]
    public async Task AnOrderWithoutVatKeepsItAfterReload()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var cityId = await EnsureCityIdAsync(client);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        using var created = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name = "Régimen Simple S.A.S.",
                bankAccounts = Array.Empty<object>(),
                taxId = "901.222.333-4",
                country = "CO",
                documentType = "NIT",
                cityId,
                chargesVat = false,
            },
            TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        using var company = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var companyId = company.RootElement.GetProperty("id").GetGuid();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders",
            new CreateOrderRequest(
                clientId, "Transferencia", null, false, null, null,
                new QuotationBillingAccountRequest(companyId, null, null, null),
                [new OrderEditItemRequest(productId, 1m)], []),
            TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode} {text}");
        using var body = JsonDocument.Parse(text);
        var orderId = body.RootElement.GetProperty("order").GetProperty("id").GetGuid();

        Assert.Equal(
            "false|null",
            await ScalarAsync<string>(
                database.GetConnectionString(),
                "SELECT billing_company_charges_vat::text || '|' || coalesce(billing_bank_name, 'null') " +
                $"FROM quotations.orders WHERE id = '{orderId}'"));
    }

    private sealed record Seeded(Guid QuotationId, Guid OrderId);

    // Una cotización enviada con empresa, cuenta y una línea de un producto con IVA 19 %, convertida en pedido.
    private static async Task<Seeded> SeedThroughTheApiAsync(string connectionString)
    {
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var vat = await CreateTaxRateAsync(client, tenantId, "IVA 19%", 19);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 119_000m, taxRateId: vat);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        using var converted = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken);
        converted.EnsureSuccessStatusCode();
        var order = await converted.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return new Seeded(quotation.Id, order.Id);
    }

    // Baja a la anterior (el Down quita las columnas nuevas) y vuelve a la última: el Up corre sobre las filas de antes.
    private static async Task RemigrateAsync(string connectionString)
    {
        await using var context = NewContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(id => id.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(target) - 1];

        await migrator.MigrateAsync(previous, TestContext.Current.CancellationToken);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'quotations' AND column_name IN ('billing_company_charges_vat', 'product_tax_percentage')"));
        await migrator.MigrateAsync(null, TestContext.Current.CancellationToken);
    }

    private static QuotationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QuotationsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "quotations"))
            .Options);

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
