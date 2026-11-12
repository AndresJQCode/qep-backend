using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El reporte de margen (spec 2026-11-10 §6.3) contra el esquema migrado. <c>MarginReportSource</c> lee
/// <c>quotations.order_items</c>, <c>pos.sale_lines</c> y <c>catalog</c> con SQL crudo, por nombre de
/// tabla y de columna: estas pruebas son lo que amarra ese SQL al esquema.
///
/// Los pedidos se siembran por la API —cotización, conversión y <c>mark-paid</c>—, porque el costo se
/// congela al marcar pagado: una línea insertada a mano no probaría lo que el sistema guarda. Los
/// productos no llevan tasa de impuesto, así que el subtotal de la línea es precio × cantidad.
/// </summary>
public sealed class MarginReportApiTests
{
    private static readonly Dictionary<string, decimal> CopPrice = new() { ["COP"] = 100_000m };
    private static readonly Dictionary<string, decimal> CopCost = new() { ["COP"] = 60_000m };
    private static readonly Dictionary<string, decimal> CopAndUsdPrice = new() { ["COP"] = 100_000m, ["USD"] = 30m };

    [Fact]
    public async Task TheSummaryAndTheListAgreeOnACostedOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        await world.PaidOrderAsync(factory, productId, quantity: 2m);

        var summary = await world.SummaryAsync();
        var page = await world.ListAsync();

        Assert.Equal(
            [new MarginCurrency("COP", 200_000m, 120_000m, 80_000m, 40.0m, 0, 0m)],
            summary.Currencies);
        Assert.Equal(1, page.Total);
        var row = Assert.Single(page.Items);
        Assert.Equal(
            (productId, "Vela de soja", "COP", 2m, 200_000m, 120_000m, 80_000m, 40.0m),
            (row.ProductId, row.ProductName, row.Currency, row.Quantity, row.NetSales, row.Cost, row.GrossProfit, row.MarginPercent));
        var variant = Assert.Single(row.Variants);
        // La variante por defecto de un producto sin opciones lleva el id del producto.
        Assert.Equal(
            new MarginVariant(productId, "Vela de soja", 2m, 200_000m, 120_000m, 80_000m, 40.0m),
            variant);

        // El contrato que el SPA ya consume (qep-frontend margin-report.ts): nombres en camelCase.
        using var summaryJson = await world.GetJsonAsync("/margin/summary");
        Assert.Equal(
            ["currency", "netSales", "cost", "grossProfit", "marginPercent", "uncostedLines", "uncostedNetSales"],
            PropertyNames(summaryJson.RootElement.GetProperty("currencies")[0]));
        using var listJson = await world.GetJsonAsync("/margin");
        Assert.Equal(["items", "total", "page", "pageSize"], PropertyNames(listJson.RootElement));
        var item = listJson.RootElement.GetProperty("items")[0];
        Assert.Equal(
            ["productId", "productName", "currency", "quantity", "netSales", "cost", "grossProfit", "marginPercent", "variants"],
            PropertyNames(item));
        Assert.Equal(
            ["variantId", "variantName", "quantity", "netSales", "cost", "grossProfit", "marginPercent"],
            PropertyNames(item.GetProperty("variants")[0]));
    }

    [Fact]
    public async Task CancelledIsOutAndArchivedIsIn()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        var cancelled = await world.PaidOrderAsync(factory, productId, quantity: 2m);
        var archived = await world.PaidOrderAsync(factory, productId, quantity: 2m);

        var cancel = await world.Client.PostAsJsonAsync(
            world.OrderUrl(cancelled.Id, "cancel"), new CancelOrderRequest("El cliente desistió."),
            TestContext.Current.CancellationToken);
        await AssertSuccessAsync(cancel);
        await world.OrderActionAsync(archived.Id, "prepare");
        await world.OrderActionAsync(archived.Id, "archive");

        var summary = await world.SummaryAsync();

        Assert.Equal(
            [new MarginCurrency("COP", 200_000m, 120_000m, 80_000m, 40.0m, 0, 0m)],
            summary.Currencies);
        Assert.Equal(2m, Assert.Single((await world.ListAsync()).Items).Quantity);
    }

    [Fact]
    public async Task UnmarkedIsOutAndRemarkedComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        var order = await world.PaidOrderAsync(factory, productId, quantity: 2m);

        await world.OrderActionAsync(order.Id, "unmark-paid");
        var unmarked = await world.SummaryAsync();
        var unmarkedPage = await world.ListAsync();
        await world.OrderActionAsync(order.Id, "mark-paid");
        var remarked = await world.SummaryAsync();

        Assert.Empty(unmarked.Currencies);
        Assert.Equal(0, unmarkedPage.Total);
        Assert.Equal(
            [new MarginCurrency("COP", 200_000m, 120_000m, 80_000m, 40.0m, 0, 0m)],
            remarked.Currencies);
    }

    [Fact]
    public async Task AVoidedPosSaleIsOut()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        await EnablePosAsync(database, world.TenantId);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        var sessionId = await OpenPosSessionAsync(world.Client, world.TenantId);
        var sale = await CreatePosSaleAsync(world.Client, world.TenantId, sessionId, productId, 100_000m);

        var sold = await world.SummaryAsync();
        var posOnly = await world.SummaryAsync("?source=Pos");
        var quotationOnly = await world.SummaryAsync("?source=Quotation");
        await VoidPosSaleAsync(world.Client, world.TenantId, sale.Id);
        var voided = await world.SummaryAsync();

        var expected = new MarginCurrency("COP", 100_000m, 60_000m, 40_000m, 40.0m, 0, 0m);
        Assert.Equal([expected], sold.Currencies);
        Assert.Equal([expected], posOnly.Currencies);
        Assert.Empty(quotationOnly.Currencies);
        Assert.Empty(voided.Currencies);
    }

    [Fact]
    public async Task AnUncostedLineIsCountedApart()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var costed = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        var uncosted = await CreateProductAsync(
            world.Client, world.TenantId, prices: new Dictionary<string, decimal> { ["COP"] = 50_000m });
        await world.PaidOrderAsync(factory, costed, quantity: 2m);
        await world.PaidOrderAsync(factory, uncosted, quantity: 1m);

        var summary = await world.SummaryAsync();
        var page = await world.ListAsync();

        Assert.Equal(
            [new MarginCurrency("COP", 200_000m, 120_000m, 80_000m, 40.0m, 1, 50_000m)],
            summary.Currencies);
        // C9: el producto sin costo no aparece en el listado.
        Assert.Equal(1, page.Total);
        Assert.Equal(costed, Assert.Single(page.Items).ProductId);
    }

    /// <summary>
    /// Requisito del frontend: una moneda en la que ninguna línea tiene costo vuelve igual, en cero y
    /// con el porcentaje nulo. Si no, el SPA pinta el estado vacío y esconde el aviso de lo que quedó
    /// fuera del margen.
    /// </summary>
    [Fact]
    public async Task AVariantWithoutCostInTheOrderCurrencyIsUncosted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(
            world.Client, world.TenantId, prices: CopAndUsdPrice, costs: CopCost);
        await world.PaidOrderAsync(factory, productId, quantity: 2m);
        await world.PaidOrderAsync(factory, productId, quantity: 1m, currency: "USD");

        var summary = await world.SummaryAsync();
        var page = await world.ListAsync();

        Assert.Equal(
            [
                new MarginCurrency("COP", 200_000m, 120_000m, 80_000m, 40.0m, 0, 0m),
                new MarginCurrency("USD", 0m, 0m, 0m, null, 1, 30m),
            ],
            summary.Currencies);
        Assert.Equal("COP", Assert.Single(page.Items).Currency);
    }

    [Fact]
    public async Task TwoCurrenciesNeverMix()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(
            world.Client,
            world.TenantId,
            prices: CopAndUsdPrice,
            costs: new Dictionary<string, decimal> { ["COP"] = 60_000m, ["USD"] = 10m });
        // El de dólares primero: el orden del listado no puede salir del orden de inserción.
        await world.PaidOrderAsync(factory, productId, quantity: 1m, currency: "USD");
        await world.PaidOrderAsync(factory, productId, quantity: 2m);

        var summary = await world.SummaryAsync();
        var page = await world.ListAsync();

        Assert.Equal(
            [
                new MarginCurrency("COP", 200_000m, 120_000m, 80_000m, 40.0m, 0, 0m),
                new MarginCurrency("USD", 30m, 10m, 20m, 66.7m, 0, 0m),
            ],
            summary.Currencies);
        Assert.Equal(2, page.Total);
        Assert.Equal(
            [("COP", 80_000m), ("USD", 20m)],
            page.Items.Select(item => (item.Currency, item.GrossProfit)));
        Assert.All(page.Items, item => Assert.Equal(productId, item.ProductId));
    }

    /// <summary>El pago de las 23:00 del 31 de diciembre en Bogotá ya es 2027 en UTC: cuenta en
    /// diciembre, que es el día del tenant.</summary>
    [Fact]
    public async Task ThePeriodFollowsTheTenantDay()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        await world.PaidOrderAsync(factory, productId, quantity: 2m, validUntil: new DateOnly(2027, 2, 15));

        var newYearsEve = await world.SummaryAsync("?from=2026-12-31&to=2026-12-31");
        var newYearsEveList = await world.ListAsync("?from=2026-12-31&to=2026-12-31");
        var newYear = await world.SummaryAsync("?from=2027-01-01&to=2027-01-01");
        var newYearList = await world.ListAsync("?from=2027-01-01&to=2027-01-01");

        Assert.Equal(200_000m, Assert.Single(newYearsEve.Currencies).NetSales);
        Assert.Equal(1, newYearsEveList.Total);
        Assert.Empty(newYear.Currencies);
        Assert.Equal(0, newYearList.Total);
    }

    /// <summary>Un pedido pagado antes de que existiera el costo no tiene líneas congeladas: aparece
    /// como sin costo, nunca con costo cero (spec §4.4).</summary>
    [Fact]
    public async Task AnOrderPaidBeforeTheDeployIsCountedAsUncosted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        var order = await world.PaidOrderAsync(factory, productId, quantity: 2m);
        await ExecuteAsync(
            database,
            """
            UPDATE quotations.order_items SET unit_cost = NULL, cost_source = NULL, cost_captured_at = NULL
            WHERE order_id = @orderId
            """,
            ("orderId", order.Id));

        var summary = await world.SummaryAsync();

        Assert.Equal(
            [new MarginCurrency("COP", 0m, 0m, 0m, null, 1, 200_000m)],
            summary.Currencies);
        Assert.Equal(0, (await world.ListAsync()).Total);
    }

    /// <summary>La fila del producto toma el nombre actual del catálogo; si el producto ya no existe,
    /// el congelado en su línea más reciente, y sigue apareciendo.</summary>
    [Fact]
    public async Task ADeletedProductStillShowsWithItsFrozenName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        var productId = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        await world.PaidOrderAsync(factory, productId, quantity: 2m);

        await ExecuteAsync(
            database,
            "UPDATE catalog.products SET name = 'Vela renombrada' WHERE id = @productId",
            ("productId", productId));
        var renamed = Assert.Single((await world.ListAsync()).Items);
        await ExecuteAsync(database, "DELETE FROM catalog.products WHERE id = @productId", ("productId", productId));
        var deleted = Assert.Single((await world.ListAsync()).Items);

        Assert.Equal("Vela renombrada", renamed.ProductName);
        Assert.Equal((productId, "Vela de soja", 80_000m), (deleted.ProductId, deleted.ProductName, deleted.GrossProfit));
        Assert.Equal("Vela de soja", Assert.Single(deleted.Variants).VariantName);
    }

    [Fact]
    public async Task TheListIsOrderedByProfitInTheDefaultCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory);
        // C primero: con ids v7 crecientes, ordenar por id pondría a C adelante.
        var productC = await CreateProductAsync(
            world.Client,
            world.TenantId,
            prices: new Dictionary<string, decimal> { ["COP"] = 50_000m },
            costs: new Dictionary<string, decimal> { ["COP"] = 40_000m });
        var productA = await CreateProductAsync(world.Client, world.TenantId, prices: CopPrice, costs: CopCost);
        await world.PaidOrderAsync(factory, productC, quantity: 1m);
        await world.PaidOrderAsync(factory, productA, quantity: 2m);

        var page = await world.ListAsync();

        Assert.Equal(
            [(productA, 80_000m), (productC, 10_000m)],
            page.Items.Select(item => (item.ProductId, item.GrossProfit)));
    }

    [Fact]
    public async Task WithoutTheMarginPermissionIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(
            factory,
            [.. MarginPermissions.Where(permission => permission != Modules.Reporting.Application.ReportingPermissions.MarginRead)]);
        using var client = tenant.Client;

        var list = await client.GetAsync($"{ReportsUrl(tenant.TenantId)}/margin", TestContext.Current.CancellationToken);
        var summary = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/margin/summary", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);
    }

    private static async Task<MarginWorld> ArrangeAsync(QepApiFactory factory)
    {
        var tenant = await RegisterTenantAsync(factory, [.. MarginPermissions, .. PosPermissions]);
        var customer = await CreateActiveCustomerAsync(tenant.Client, tenant.TenantId);
        return new MarginWorld(tenant, customer.Id);
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];

    private static async Task AssertSuccessAsync(HttpResponseMessage response) =>
        Assert.True(
            response.IsSuccessStatusCode,
            $"Se esperaba 2xx y llegó {(int)response.StatusCode}: "
                + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task ExecuteAsync(
        PostgreSqlContainer database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.True(await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken) > 0);
    }

    private sealed record MarginWorld(RegisteredTenant Tenant, Guid CustomerId)
    {
        public Guid TenantId => Tenant.TenantId;

        public HttpClient Client => Tenant.Client;

        public string OrderUrl(Guid orderId, string action) =>
            $"/api/v1/tenants/{TenantId}/orders/{orderId}/{action}";

        /// <summary>Cotiza, convierte y marca pagado: el costo se congela en el <c>mark-paid</c>.</summary>
        public async Task<OrderResponse> PaidOrderAsync(
            QepApiFactory factory,
            Guid productId,
            decimal quantity,
            string currency = "COP",
            DateOnly? validUntil = null)
        {
            var quotation = await CreateSentQuotationAsync(
                Client, factory, TenantId, CustomerId, productId, validUntil, currency, quantity: quantity);
            var order = await ConvertToOrderAsync(Client, factory, TenantId, quotation);
            await OrderActionAsync(order.Id, "mark-paid");
            return order;
        }

        public async Task OrderActionAsync(Guid orderId, string action)
        {
            var response = await Client.PostAsync(
                OrderUrl(orderId, action), content: null, TestContext.Current.CancellationToken);
            await AssertSuccessAsync(response);
        }

        public async Task<MarginSummary> SummaryAsync(string query = "")
        {
            var summary = await Client.GetFromJsonAsync<MarginSummary>(
                $"{ReportsUrl(TenantId)}/margin/summary{query}", TestContext.Current.CancellationToken);
            Assert.NotNull(summary);
            return summary;
        }

        public async Task<ReportPageDto<MarginProduct>> ListAsync(string query = "")
        {
            var page = await Client.GetFromJsonAsync<ReportPageDto<MarginProduct>>(
                $"{ReportsUrl(TenantId)}/margin{query}", TestContext.Current.CancellationToken);
            Assert.NotNull(page);
            return page;
        }

        public async Task<JsonDocument> GetJsonAsync(string path)
        {
            var body = await Client.GetStringAsync(
                $"{ReportsUrl(TenantId)}{path}", TestContext.Current.CancellationToken);
            return JsonDocument.Parse(body);
        }
    }
}

/// <summary>Copias propias del contrato del reporte de margen: ver <see cref="ReportPageDto{TItem}"/>.</summary>
internal sealed record MarginSummary(IReadOnlyList<MarginCurrency> Currencies);

internal sealed record MarginCurrency(
    string Currency,
    decimal NetSales,
    decimal Cost,
    decimal GrossProfit,
    decimal? MarginPercent,
    int UncostedLines,
    decimal UncostedNetSales);

internal sealed record MarginProduct(
    Guid ProductId,
    string ProductName,
    string Currency,
    decimal Quantity,
    decimal NetSales,
    decimal Cost,
    decimal GrossProfit,
    decimal? MarginPercent,
    IReadOnlyList<MarginVariant> Variants);

internal sealed record MarginVariant(
    Guid VariantId,
    string VariantName,
    decimal Quantity,
    decimal NetSales,
    decimal Cost,
    decimal GrossProfit,
    decimal? MarginPercent);
