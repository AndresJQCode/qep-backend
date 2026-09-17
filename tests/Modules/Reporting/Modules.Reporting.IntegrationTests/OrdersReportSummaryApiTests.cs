using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El resumen agregado de pedidos: <c>GET /reports/orders/summary</c>.
///
/// Estas pruebas existen sobre todo por una razon que ninguna unitaria puede cubrir: **que los
/// agregados se traduzcan a SQL**. Sumas, <c>GROUP BY</c> por mes sobre una columna
/// <c>timestamptz</c> y agrupacion por una propiedad con conversor de valor son justo las tres
/// cosas que compilan perfecto y explotan en tiempo de ejecucion contra PostgreSQL. Con el
/// handler probado aparte, lo que se verifica aca es que la consulta corra y de los numeros.
/// </summary>
public sealed class OrdersReportSummaryApiTests
{
    [Fact]
    public async Task SummaryAddsUpTheTenantsOrdersAndRanksAdvisorAndClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<OrdersReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);

        Assert.Equal(1, summary.OrderCount);
        Assert.Equal(quotation.Subtotal, summary.Subtotal);
        Assert.Equal(quotation.TaxAmount, summary.TaxAmount);
        Assert.Equal(quotation.Total, summary.Total);

        // La serie mensual: un solo mes, el de la conversion, y sin rellenar los vacios.
        var month = Assert.Single(summary.Monthly);
        Assert.Equal(1, month.Count);
        Assert.Equal(quotation.Total, month.Total);

        // El ranking por asesor agrupa sobre una propiedad con conversor de valor (MemberId) y
        // resuelve la etiqueta al email, igual que el listado.
        var advisor = Assert.Single(summary.ByAdvisor);
        Assert.NotNull(advisor.Id);
        Assert.Equal(tenant.OwnerEmail, advisor.Label);
        Assert.Equal(1, advisor.EntityCount);
        Assert.Equal(1, advisor.Count);
        Assert.Equal(quotation.Total, advisor.Total);

        var rankedClient = Assert.Single(summary.ByClient);
        Assert.Equal(customer.Id, rankedClient.Id);
        Assert.Equal("Verde Esencial S.A.S.", rankedClient.Label);
        Assert.Equal(customer.Cuc, rankedClient.Secondary);
        Assert.Equal(quotation.Total, rankedClient.Total);

        // Sin rango de fechas no hay periodo anterior contra el cual comparar.
        Assert.Null(summary.Previous);
    }

    /// <summary>
    /// Spec 2026-09-16, decisión 6: un pedido anulado no es una venta. Dos pedidos del mismo
    /// cliente y asesor, uno anulado: el resumen cuenta uno solo en el total, la serie y los dos
    /// rankings. Filtrar en la consulta y no en memoria es lo que esta prueba cubre contra
    /// PostgreSQL.
    /// </summary>
    [Fact]
    public async Task SummaryLeavesOutACancelledOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var kept = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, kept);
        var cancelled = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var cancelledOrder = await ConvertToOrderAsync(client, factory, tenant.TenantId, cancelled);
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/orders/{cancelledOrder.Id}/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<OrdersReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.OrderCount);
        Assert.Equal(kept.Subtotal, summary.Subtotal);
        Assert.Equal(kept.TaxAmount, summary.TaxAmount);
        Assert.Equal(kept.Total, summary.Total);
        Assert.Equal(1, Assert.Single(summary.Monthly).Count);
        Assert.Equal(1, Assert.Single(summary.ByAdvisor).Count);
        Assert.Equal(1, Assert.Single(summary.ByClient).Count);
    }

    /// <summary>
    /// Un tenant sin pedidos devuelve ceros y listas vacias, **no un 404 ni un cuerpo nulo**: el
    /// panel tiene que poder distinguir "no hay pedidos" de "no se pudo cargar", y un agregado
    /// sobre cero filas es un caso valido, no un error.
    /// </summary>
    [Fact]
    public async Task SummaryReturnsZerosWhenTheTenantHasNoOrders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<OrdersReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(0, summary.OrderCount);
        Assert.Equal(0m, summary.Total);
        Assert.Empty(summary.Monthly);
        Assert.Empty(summary.ByAdvisor);
        Assert.Empty(summary.ByClient);
    }

    /// <summary>
    /// Con rango, la respuesta trae el periodo anterior. Acá la ventana previa está vacía, que es
    /// justamente el caso que mas rompe: el agregado de un conjunto vacio tiene que dar cero y no
    /// nulo, o el delta del panel queda sin base.
    /// </summary>
    [Fact]
    public async Task SummaryWithADateRangeCarriesTheComparisonAgainstTheWindowBefore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-29);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary?from={from:yyyy-MM-dd}&to={today:yyyy-MM-dd}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<OrdersReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.OrderCount);

        Assert.NotNull(summary.Previous);
        Assert.Equal(0, summary.Previous.Count);
        Assert.Equal(0m, summary.Previous.Total);
    }

    /// <summary>403 y no 404, igual que el listado: un 404 confirmaria que el tenant existe.</summary>
    [Fact]
    public async Task SummaryRejectsAnotherTenantsReport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(Guid.CreateVersion7())}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("authorization.denied", problem?.Code);
    }

    /// <summary>El resumen no afloja el permiso: es el mismo <c>reporting.orders.read</c> del
    /// listado, porque expone los mismos datos sumados.</summary>
    [Fact]
    public async Task SummaryRejectsACallerWithoutTheReportingPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, SeedOnlyPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Un estado de pago inexistente es 422 con el mapa <c>errors</c>, igual que en el
    /// listado: el frontend necesita saber que control marcar.</summary>
    [Fact]
    public async Task SummaryRejectsAnUnknownPaymentStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders/summary?paymentStatus=Refunded",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
