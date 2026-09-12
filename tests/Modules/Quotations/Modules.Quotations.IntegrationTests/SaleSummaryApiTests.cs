using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El panel del listado de ventas: <c>GET /sales/summary</c>. No confundirlo con el resumen del
/// reporte de ventas de Reporting (<c>/reports/sales/summary</c>): este no lleva los filtros de la
/// grilla, sólo la ventana de fechas.
///
/// Lo que ninguna unitaria cubre y estas sí: que el agrupado por estado y la suma de comprobantes
/// por subconsulta se traduzcan a SQL contra PostgreSQL, y que el handler esté registrado en el
/// contenedor -- que es justo lo que faltaba hasta 6e80c12. Lo detectó <c>CompositionRootTests</c>,
/// pero ninguna prueba ejercía la ruta.
/// </summary>
public sealed class SaleSummaryApiTests
{
    private static string SummaryUrl(Guid tenantId, DateOnly from, DateOnly to) =>
        $"/api/v1/tenants/{tenantId}/sales/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

    // Productos con precios distintos a propósito: con el mismo total en las dos ventas, un
    // pendiente y un aprobado intercambiados darían los mismos números y la prueba pasaría igual.
    // Los comprobantes, además, suman algo que no coincide con ningún total: así se ve que
    // `collectedTotal` sale de los comprobantes y no del total de la cotización.
    [Fact]
    public async Task SummarySplitsTheWindowsSalesIntoPendingAndApproved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var cheapProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var expensiveProductId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 250_000m);
        var pending = await CreateSentQuotationAsync(client, factory, tenantId, clientId, cheapProductId);
        var pendingSale = await ConvertToSaleAsync(client, factory, tenantId, pending.Id, proofAmount: 30_000m);
        var approved = await CreateSentQuotationAsync(client, factory, tenantId, clientId, expensiveProductId);
        var approvedSale = await ConvertToSaleAsync(client, factory, tenantId, approved.Id, proofAmount: 45_000m);
        await ApproveAsync(client, tenantId, approved.Id);

        // La ventana sale de la fecha real de cada venta y no de "hoy": si la prueba cruza la
        // medianoche UTC entre las dos conversiones, una fecha fija dejaría una afuera.
        var from = ConvertedOn(pendingSale);
        var to = ConvertedOn(approvedSale);
        var response = await client.GetAsync(
            SummaryUrl(tenantId, from, to), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<SaleSummaryResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(2, summary.SaleCount);
        Assert.Equal(pending.Total + approved.Total, summary.Total);
        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(pending.Total, summary.PendingTotal);
        Assert.Equal(1, summary.ApprovedCount);
        Assert.Equal(approved.Total, summary.ApprovedTotal);
        Assert.Equal(75_000m, summary.CollectedTotal);
    }

    // El reloj no se puede mover: `ConvertedAt` lo pone el handler al convertir y el factory no
    // sustituye el reloj. Así que en vez de llevar la venta al pasado se lleva la ventana al
    // futuro: pidiendo la semana que empieza siete días después, la anterior de la misma longitud
    // arranca justo el día de la venta. Que la venta caiga en el **primer** día de esa ventana
    // anterior es a propósito: una longitud mal calculada por uno la dejaría afuera.
    [Fact]
    public async Task SummaryCountsASaleOfTheWindowBeforeOnlyInThePreviousTotal()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var sale = await ConvertToSaleAsync(client, factory, tenantId, quotation.Id);

        var convertedOn = ConvertedOn(sale);
        var response = await client.GetAsync(
            SummaryUrl(tenantId, convertedOn.AddDays(7), convertedOn.AddDays(13)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<SaleSummaryResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(0, summary.SaleCount);
        Assert.Equal(0m, summary.Total);
        Assert.Equal(quotation.Total, summary.PreviousTotal);
    }

    // Un rango invertido es un 422 con código de dominio, no una ventana vacía que en silencio
    // devuelve ceros: el panel mostraría "no vendiste nada" por un error de quien arma la URL.
    [Fact]
    public async Task SummaryRejectsARangeThatEndsBeforeItStarts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.GetAsync(
            SummaryUrl(tenantId, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("sale.summary.range_invalid", problem.Code);
    }

    // Todos los permisos del harness menos el de leer ventas, y no sólo los de cotizaciones: así
    // se ve que ni siquiera `sales.sale.manage` alcanza. El panel expone los mismos importes que
    // el listado, así que pide el mismo permiso.
    [Fact]
    public async Task SummaryRequiresThePermissionToReadSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory,
            ManagerPermissions.Where(permission => permission != SalesPermissions.SaleRead).ToArray());
        using var _ = client;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await client.GetAsync(
            SummaryUrl(tenantId, today, today), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // 403 y no 404, igual que el resto de las rutas de ventas: un 404 confirmaría que el tenant
    // existe.
    [Fact]
    public async Task SummaryForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var (_, _, otherOwner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherOwner;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var response = await otherOwner.GetAsync(
            SummaryUrl(tenantId, today, today), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El otro lado del aislamiento: pidiendo su propio tenant, nadie ve ventas ajenas aunque
    // caigan en la misma ventana -- el filtro por tenant de la consulta, que el 403 de arriba no
    // ejerce. Cubre también el caso vacío: ceros y 200, **no un 404 ni un cuerpo nulo**, porque
    // el panel tiene que distinguir "no hay ventas" de "no se pudo cargar".
    [Fact]
    public async Task SummaryDoesNotCountAnotherTenantsSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, owner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = owner;
        var clientId = await CreateActiveCustomerAsync(owner, tenantId);
        var productId = await CreateProductWithScalesAsync(owner, tenantId);
        var quotation = await CreateSentQuotationAsync(owner, factory, tenantId, clientId, productId);
        var sale = await ConvertToSaleAsync(owner, factory, tenantId, quotation.Id, proofAmount: 30_000m);
        var (otherTenantId, _, otherOwner) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var __ = otherOwner;

        var convertedOn = ConvertedOn(sale);
        var response = await otherOwner.GetAsync(
            SummaryUrl(otherTenantId, convertedOn, convertedOn),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<SaleSummaryResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(new SaleSummaryResponse(0, 0m, 0, 0m, 0, 0m, 0m, 0m), summary);
    }

    /// <summary>El día UTC de la conversión, que es contra lo que la consulta compara la
    /// ventana.</summary>
    private static DateOnly ConvertedOn(SaleResponse sale) =>
        DateOnly.FromDateTime(sale.ConvertedAt.UtcDateTime);

    /// <summary>Sin <paramref name="proofAmount"/> convierte con el pago pendiente, el único caso
    /// en el que la conversión no exige comprobantes. Con él, adjunta uno solo por ese importe
    /// como pago parcial, que no obliga a que el comprobante cubra el total.</summary>
    private static async Task<SaleResponse> ConvertToSaleAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, Guid quotationId, decimal? proofAmount = null)
    {
        ConvertQuotationToSaleRequest request;
        if (proofAmount is { } amount)
        {
            var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
            request = new ConvertQuotationToSaleRequest(
                "PartialPaymentReceived", null, [new SalePaymentProofRequest(proofFileId, amount)]);
        }
        else
        {
            request = new ConvertQuotationToSaleRequest("PaymentPending", null, []);
        }

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale",
            request,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(sale);
        return sale;
    }

    private static async Task ApproveAsync(HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/sale/approve",
            null,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private sealed record ProblemPayload(string Code);
}
