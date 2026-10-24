using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Las ventas POS dentro del reporte de pedidos (spec 2026-10-23): <c>OrdersReportSource</c> lee la
/// unión de <c>quotations.orders</c> y <c>pos.sales</c> con SQL crudo, por nombre de tabla y de
/// columna. Estas pruebas son lo que amarra ese SQL al esquema migrado: renombrar una columna de
/// cualquiera de las dos tablas compila y deja <c>/reports/orders</c> respondiendo 500.
///
/// Las ventas se siembran por la API del POS, igual que los pedidos por la de Quotations: una fila
/// insertada a mano probaría la consulta, pero no que la venta que el sistema produce tenga lo que
/// el reporte espera encontrar.
/// </summary>
public sealed class OrdersReportPosApiTests
{
    private const decimal PosPrice = 30_000m;

    [Fact]
    public async Task ListIncludesPosSalesWithTheirOwnShape()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        await world.CreateQuotationOrderAsync(factory);
        var sale = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice);

        var page = await world.Client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(world.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(2, page.Total);
        var row = Assert.Single(page.Items, item => item.OrderId == sale.Id);
        Assert.Equal("Pos", row.Source);
        Assert.StartsWith("POS-", row.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(sale.SaleNumber, row.OrderNumber);
        Assert.Null(row.QuotationId);
        Assert.Null(row.QuotationNumber);
        Assert.Null(row.ClientId);
        Assert.Equal("Consumidor final", row.ClientName);
        Assert.Null(row.ClientCuc);
        Assert.Equal("Completed", row.Status);
        Assert.Equal("FullPaymentReceived", row.PaymentStatus);
        Assert.Equal("COP", row.Currency);
        Assert.Equal(0m, row.FreightSubtotal);
        Assert.Equal(0m, row.FreightTaxAmount);
        Assert.Equal(PosPrice, row.Total);
        Assert.Equal(row.Total, row.Subtotal + row.TaxAmount);
        Assert.Equal(world.OwnerMembershipId, row.AdvisorId);
        Assert.Equal(world.OwnerEmail, row.AdvisorName);
    }

    [Fact]
    public async Task PosSaleWithACustomerCarriesTheCustomersNameAndCuc()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var customer = await CreateActiveCustomerAsync(world.Client, world.TenantId);
        var sale = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice, customer.Id);

        var page = await world.Client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(world.TenantId)}/orders", TestContext.Current.CancellationToken);

        var row = Assert.Single(page!.Items, item => item.OrderId == sale.Id);
        Assert.Equal(customer.Id, row.ClientId);
        Assert.Equal("Verde Esencial S.A.S.", row.ClientName);
        Assert.Equal(customer.Cuc, row.ClientCuc);
    }

    /// <summary>Lo anulado no suma, pero se ve: igual que un pedido cancelado.</summary>
    [Fact]
    public async Task VoidedPosSaleIsListedAsVoided()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var sale = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice);
        await VoidPosSaleAsync(world.Client, world.TenantId, sale.Id);

        var page = await world.Client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(world.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.Equal("Voided", Assert.Single(page!.Items, item => item.OrderId == sale.Id).Status);
    }

    /// <summary>
    /// Los filtros del reporte valen igual para las dos fuentes (spec 2026-10-23 §1): una venta POS
    /// siempre está pagada, nunca coincide con el cliente de un pedido y cae en el día de su
    /// creación. Review Focus 1: cada filtro opcional corre contra PostgreSQL sin
    /// «could not determine data type of parameter».
    /// </summary>
    [Fact]
    public async Task PosFiltersApplyToSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var order = await world.CreateQuotationOrderAsync(factory);
        var sale = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice);
        var today = TodayInBogota();
        var outside = today.AddDays(-10);

        (string Query, bool HasSale, bool HasOrder)[] cases =
        [
            ("source=Pos", true, false),
            ("source=Quotation", false, true),
            ("paymentStatus=PaymentPending", false, false),
            ("paymentStatus=FullPaymentReceived", true, true),
            ($"clientId={order.ClientId}", false, true),
            ($"from={outside:yyyy-MM-dd}&to={outside:yyyy-MM-dd}", false, false),
            ($"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}", true, true),
            ($"advisorId={world.OwnerMembershipId}", true, true),
        ];

        foreach (var (query, hasSale, hasOrder) in cases)
        {
            var page = await world.Client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
                $"{ReportsUrl(world.TenantId)}/orders?{query}", TestContext.Current.CancellationToken);
            Assert.NotNull(page);
            Assert.True(
                hasSale == page.Items.Any(item => item.OrderId == sale.Id),
                $"{query}: expected the POS sale {(hasSale ? "in" : "out")}.");
            Assert.True(
                hasOrder == page.Items.Any(item => item.OrderId == order.Id),
                $"{query}: expected the order {(hasOrder ? "in" : "out")}.");
            Assert.Equal(page.Items.Count, page.Total);
        }
    }

    /// <summary>
    /// Review Focus 2. Sin <c>reporting.all_advisors.read</c> el handler fija el asesor a quien
    /// llama, y el adaptador lo aplica a <c>advisor_id</c> de los pedidos **y** a <c>cashier_id</c>
    /// de las ventas: pedir el <c>advisorId</c> del dueño no le muestra las ventas del dueño.
    /// </summary>
    [Fact]
    public async Task AnAdvisorWithoutAllAdvisorsSeesOnlyTheirOwnPosSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var advisor = await InviteActiveAdvisorAsync(
            factory,
            database,
            world.Tenant,
            [.. OwnAdvisorPermissions, .. PosPermissions.Where(permission => permission != "pos.register.read")]);
        using var advisorClient = advisor.Client;
        var advisorSession = await OpenPosSessionAsync(advisorClient, world.TenantId);
        var own = await CreatePosSaleAsync(
            advisorClient, world.TenantId, advisorSession, world.PosProductId, PosPrice);
        var owners = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice);

        var page = await advisorClient.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(world.TenantId)}/orders?advisorId={world.OwnerMembershipId}",
            TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        var row = Assert.Single(page.Items);
        Assert.Equal(own.Id, row.OrderId);
        Assert.Equal(advisor.MembershipId, row.AdvisorId);
        Assert.DoesNotContain(page.Items, item => item.OrderId == owners.Id);
    }

    /// <summary>
    /// Review Focus 5: un solo orden total sobre las dos tablas (fecha descendente, número, id).
    /// Tres pedidos y tres ventas en páginas de dos: las tres páginas juntas dan los seis ids, sin
    /// repetir ni perder ninguno.
    /// </summary>
    [Fact]
    public async Task PagesNeverRepeatOrSkipRowsAcrossBothSources()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var expected = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            expected.Add((await world.CreateQuotationOrderAsync(factory)).Id);
            expected.Add((await CreatePosSaleAsync(
                world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice)).Id);
        }

        var seen = new List<Guid>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await world.Client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
                $"{ReportsUrl(world.TenantId)}/orders?page={pageNumber}&pageSize=2",
                TestContext.Current.CancellationToken);
            Assert.NotNull(page);
            Assert.Equal(6, page.Total);
            Assert.Equal(2, page.Items.Count);
            seen.AddRange(page.Items.Select(item => item.OrderId));
        }

        Assert.Equal(expected.Order(), seen.Order());
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task ListRejectsAnotherTenantsReportWith403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);

        var response = await world.Client.GetAsync(
            $"{ReportsUrl(Guid.CreateVersion7())}/orders?source=Pos", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Spec 2026-10-23 §3: el resumen suma las dos fuentes en totales, serie, ranking por asesor (el
    /// cajero entra como un asesor más) y <c>bySource</c>.
    /// </summary>
    [Fact]
    public async Task SummaryAddsPosSalesEverywhere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var order = await world.CreateQuotationOrderAsync(factory);
        var customer = await CreateActiveCustomerAsync(world.Client, world.TenantId);
        var sale = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice, customer.Id);

        var summary = await GetSummaryAsync(world);

        Assert.Equal(2, summary.OrderCount);
        Assert.Equal([new MoneyAmount("COP", order.Total + sale.Total)], summary.Totals);
        Assert.Equal([new MoneyAmount("COP", order.Subtotal + sale.Subtotal)], summary.Subtotals);
        Assert.Equal([new MoneyAmount("COP", order.TaxAmount + sale.TaxAmount)], summary.TaxAmounts);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal(2, month.Count);
        Assert.Equal([new MoneyAmount("COP", order.Total + sale.Total)], month.Totals);
        var advisor = Assert.Single(summary.ByAdvisor);
        Assert.Equal(world.OwnerMembershipId, advisor.Id);
        Assert.Equal(world.OwnerEmail, advisor.Label);
        Assert.Equal(2, advisor.Count);
        Assert.Equal(2, summary.ByClient.Count);
        Assert.Contains(summary.ByClient, entry => entry.Id == customer.Id && entry.Count == 1);
        Assert.Equal(["Quotation", "Manual", "Shopify", "Pos"], summary.BySource.Select(slice => slice.Source));
        Assert.Equal([1, 0, 0, 1], summary.BySource.Select(slice => slice.Count));
        Assert.Equal([new MoneyAmount("COP", sale.Total)], summary.BySource[3].Totals);
        Assert.Equal(0, summary.WithoutClient.Count);
    }

    /// <summary>Una venta anulada no es una venta: no suma en ningún agregado.</summary>
    [Fact]
    public async Task VoidedPosSaleDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var order = await world.CreateQuotationOrderAsync(factory);
        var sale = await CreatePosSaleAsync(
            world.Client, world.TenantId, world.SessionId, world.PosProductId, PosPrice);
        await VoidPosSaleAsync(world.Client, world.TenantId, sale.Id);

        var summary = await GetSummaryAsync(world);

        Assert.Equal(1, summary.OrderCount);
        Assert.Equal([new MoneyAmount("COP", order.Total)], summary.Totals);
        Assert.Equal(0, summary.BySource[3].Count);
        Assert.Empty(summary.BySource[3].Totals);
        Assert.Equal(1, Assert.Single(summary.ByAdvisor).Count);
        Assert.Equal(1, Assert.Single(summary.Monthly).Count);
        Assert.Equal(0, summary.WithoutClient.Count);
        Assert.Empty(summary.WithoutClient.Totals);
    }

    /// <summary>
    /// Review Focus 4. Las ventas a consumidor final no tienen ficha: van a <c>withoutClient</c> y no
    /// al ranking. Con menos de cinco clientes no hay fila «Otros», así que <c>byClient</c> +
    /// <c>withoutClient</c> tiene que cerrar contra el total sin ella.
    /// </summary>
    [Fact]
    public async Task FinalConsumerSalesGoToWithoutClientNotToTheRanking()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        await world.CreateQuotationOrderAsync(factory);
        await world.CreateQuotationOrderAsync(factory);
        await SellToFinalConsumerAsync(world, 10_000m);
        await SellToFinalConsumerAsync(world, 20_000m);

        var summary = await GetSummaryAsync(world);

        Assert.Equal(2, summary.ByClient.Count);
        Assert.DoesNotContain(summary.ByClient, entry => entry.Id is null);
        Assert.Equal(2, summary.WithoutClient.Count);
        Assert.Equal([new MoneyAmount("COP", 30_000m)], summary.WithoutClient.Totals);
        Assert.Equal(
            summary.OrderCount,
            summary.ByClient.Sum(entry => entry.Count) + summary.WithoutClient.Count);
        Assert.Equal(
            summary.Totals.Single().Amount,
            summary.ByClient.Sum(entry => entry.Totals.Single().Amount) + summary.WithoutClient.Totals.Single().Amount);
    }

    /// <summary>
    /// La variante con más clientes que el tope: la fila «Otros» pliega sólo al sexto cliente, sin
    /// las ventas de mostrador en su conteo ni en su total.
    /// </summary>
    [Fact]
    public async Task FinalConsumerSalesStayOutOfTheOthersRow()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        var orders = new List<Modules.Quotations.Application.OrderResponse>();
        for (var index = 0; index < 6; index++)
        {
            orders.Add(await world.CreateQuotationOrderAsync(factory));
        }

        await SellToFinalConsumerAsync(world, 10_000m);
        await SellToFinalConsumerAsync(world, 20_000m);

        var summary = await GetSummaryAsync(world);

        Assert.Equal(8, summary.OrderCount);
        Assert.Equal(6, summary.ByClient.Count);
        var others = summary.ByClient[^1];
        Assert.Null(others.Id);
        Assert.Equal(1, others.EntityCount);
        Assert.Equal(1, others.Count);
        Assert.Equal([new MoneyAmount("COP", orders[0].Total)], others.Totals);
        Assert.Equal((2, 30_000m), (summary.WithoutClient.Count, summary.WithoutClient.Totals.Single().Amount));
        Assert.Equal(
            summary.Totals.Single().Amount,
            summary.ByClient.Sum(entry => entry.Totals.Sum(money => money.Amount))
                + summary.WithoutClient.Totals.Single().Amount);
    }

    /// <summary>
    /// Review Focus 3: la venta de las 23:00 del 31 de diciembre en Bogotá —ya enero en UTC— cae en
    /// la serie de diciembre del tenant, igual que un pedido.
    /// </summary>
    [Fact]
    public async Task PosSaleOnNewYearsEveCountsInDecember()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var world = await ArrangeAsync(factory, database);
        await SellToFinalConsumerAsync(world, PosPrice);

        var summary = await GetSummaryAsync(world, "from=2026-12-01&to=2026-12-31");

        Assert.Equal(1, summary.OrderCount);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal((2026, 12), (month.Year, month.Month));
    }

    /// <summary>El período anterior pasa por la misma base: una venta en la ventana anterior se
    /// cuenta en <c>previous</c>.</summary>
    [Fact]
    public async Task PreviousWindowIncludesPosSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var world = await ArrangeAsync(factory, database);
        await SellToFinalConsumerAsync(world, PosPrice);

        var summary = await GetSummaryAsync(world, "from=2027-01-01&to=2027-01-31");

        Assert.Equal(0, summary.OrderCount);
        Assert.NotNull(summary.Previous);
        Assert.Equal(1, summary.Previous.Count);
        Assert.Equal([new MoneyAmount("COP", PosPrice)], summary.Previous.Totals);
    }

    [Fact]
    public async Task SourcePosLeavesOnlySales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        await world.CreateQuotationOrderAsync(factory);
        await SellToFinalConsumerAsync(world, PosPrice);

        var summary = await GetSummaryAsync(world, "source=Pos");

        Assert.Equal(1, summary.OrderCount);
        Assert.Equal([new MoneyAmount("COP", PosPrice)], summary.Totals);
        Assert.Equal([0, 0, 0, 1], summary.BySource.Select(slice => slice.Count));
        Assert.Empty(summary.ByClient);
        Assert.Equal(1, summary.WithoutClient.Count);
    }

    /// <summary>
    /// <c>source=Pos</c> con un estado de pago que una venta nunca tiene omite las dos ramas de la
    /// unión: la base queda en un <c>SELECT</c> vacío con las mismas columnas, y el listado y el
    /// resumen responden ceros, no 500.
    /// </summary>
    [Fact]
    public async Task PosSourceWithAnUnpaidStatusIsAnEmptyReportNotAnError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await ArrangeAsync(factory, database);
        await world.CreateQuotationOrderAsync(factory);
        await SellToFinalConsumerAsync(world, PosPrice);
        const string query = "source=Pos&paymentStatus=PaymentPending";

        var page = await world.Client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(world.TenantId)}/orders?{query}", TestContext.Current.CancellationToken);
        var summary = await GetSummaryAsync(world, query);

        Assert.NotNull(page);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
        Assert.Equal(0, summary.OrderCount);
        Assert.Equal([0, 0, 0, 0], summary.BySource.Select(slice => slice.Count));
        Assert.Equal(0, summary.WithoutClient.Count);
    }

    private static async Task<OrdersReportSummary> GetSummaryAsync(PosWorld world, string query = "")
    {
        var summary = await world.Client.GetFromJsonAsync<OrdersReportSummary>(
            $"{ReportsUrl(world.TenantId)}/orders/summary?{query}", TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        return summary;
    }

    private static async Task SellToFinalConsumerAsync(PosWorld world, decimal price)
    {
        var productId = await CreateProductAsync(world.Client, world.TenantId, baseCop: price);
        await CreatePosSaleAsync(world.Client, world.TenantId, world.SessionId, productId, price);
    }

    /// <summary>Un tenant con <c>pos</c> prendido, una caja abierta del dueño y un producto sin IVA
    /// para vender.</summary>
    internal static async Task<PosWorld> ArrangeAsync(QepApiFactory factory, Testcontainers.PostgreSql.PostgreSqlContainer database)
    {
        var tenant = await RegisterTenantAsync(factory, [.. ManagerPermissions, .. PosPermissions]);
        await EnablePosAsync(database, tenant.TenantId);
        var productId = await CreateProductAsync(tenant.Client, tenant.TenantId, baseCop: PosPrice);
        var sessionId = await OpenPosSessionAsync(tenant.Client, tenant.TenantId);
        var membershipId = await FindOwnerMembershipIdAsync(database, tenant);
        return new PosWorld(tenant, productId, sessionId, membershipId);
    }

    private static async Task<Guid> FindOwnerMembershipIdAsync(
        Testcontainers.PostgreSql.PostgreSqlContainer database, RegisteredTenant tenant)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            connection);
        command.Parameters.AddWithValue("tenantId", tenant.TenantId);
        command.Parameters.AddWithValue("userId", tenant.OwnerUserId);
        var id = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(id);
        return (Guid)id;
    }

    internal sealed record PosWorld(RegisteredTenant Tenant, Guid PosProductId, Guid SessionId, Guid OwnerMembershipId)
    {
        public Guid TenantId => Tenant.TenantId;

        public HttpClient Client => Tenant.Client;

        public string OwnerEmail => Tenant.OwnerEmail;

        /// <summary>Un pedido convertido desde una cotización enviada, de un cliente nuevo.</summary>
        public async Task<Modules.Quotations.Application.OrderResponse> CreateQuotationOrderAsync(QepApiFactory factory)
        {
            var customer = await CreateActiveCustomerAsync(Client, TenantId);
            var productId = await CreateProductAsync(Client, TenantId);
            var quotation = await CreateSentQuotationAsync(Client, factory, TenantId, customer.Id, productId);
            return await ConvertToOrderAsync(Client, factory, TenantId, quotation);
        }
    }
}
