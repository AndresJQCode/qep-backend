using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// Reporte 1: pedidos convertidos.
///
/// La siembra pasa por los endpoints reales de Customers, Catalog y Quotations, no por SQL: el
/// reporte cruza cuatro modulos, y una fila insertada a mano probaria la consulta pero no que el
/// pedido que el sistema produce tenga los datos que el reporte espera encontrar.
/// </summary>
public sealed class OrdersReportApiTests
{
    [Fact]
    public async Task ListReturnsTheConvertedOrderWithItsQuotationAdvisorAndClient()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var order = await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(50, page.PageSize);

        var item = Assert.Single(page.Items);
        Assert.Equal(order.Id, item.OrderId);
        Assert.Equal(order.OrderNumber, item.OrderNumber);
        Assert.Equal(quotation.Id, item.QuotationId);
        Assert.Equal(quotation.QuotationNumber, item.QuotationNumber);
        // Todo pedido nace Pending y otro rol lo aprueba (aa020a8): recién convertido, así sale.
        Assert.Equal("Pending", item.Status);
        Assert.Equal("FullPaymentReceived", item.PaymentStatus);
        Assert.Equal(customer.Id, item.ClientId);
        Assert.Equal(customer.Cuc, item.ClientCuc);
        Assert.Equal("Verde Esencial S.A.S.", item.ClientName);
        // advisorName es el email: el nombre de la membresia llega al PDF y al listado de
        // cotizaciones, no a los reportes (spec 2026-09-11, D1). Ver la seccion del contrato al
        // respecto.
        Assert.Equal(tenant.OwnerEmail, item.AdvisorName);
        Assert.Equal(quotation.Subtotal, item.Subtotal);
        Assert.Equal(quotation.TaxAmount, item.TaxAmount);
        Assert.Equal(quotation.Total, item.Total);
    }

    /// <summary>
    /// Spec 2026-09-16, decisión 6: el listado sí muestra un pedido anulado, con su estado. Es la
    /// otra mitad de <c>OrdersReportSummaryApiTests.SummaryLeavesOutACancelledOrder</c>: el filtro
    /// del resumen no puede colarse en la consulta del listado.
    /// </summary>
    [Fact]
    public async Task ListStillReturnsACancelledOrderWithItsStatus()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(
            factory, [.. ManagerPermissions, OrdersPermissions.OrderCancel]);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);
        var order = await ConvertToOrderAsync(client, factory, tenant.TenantId, quotation);
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant.TenantId}/orders/{order.Id}/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal(order.Id, item.OrderId);
        Assert.Equal("Cancelled", item.Status);
    }

    [Fact]
    public async Task ListReturnsAnEmptyPageWhenTheTenantHasNoOrders()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    /// <summary>403 y no 404: un 404 confirmaria que el tenant de la ruta existe.</summary>
    [Fact]
    public async Task ListRejectsAnotherTenantsReport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var otherTenantId = Guid.CreateVersion7();

        var response = await client.GetAsync(
            $"{ReportsUrl(otherTenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("authorization.denied", problem?.Code);
    }

    /// <summary>El 403 tiene que venir del permiso de reporting que falta, no de otro: por eso el
    /// cliente lleva todos los de siembra y ninguno de los cuatro de Reporting.</summary>
    [Fact]
    public async Task ListRejectsACallerWithoutTheReportingPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, SeedOnlyPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListRejectsAPaymentStatusThatDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/orders?paymentStatus=Refunded",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
    }

    [Fact]
    public async Task ListFiltersByPaymentStatus()
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

        var matching = await client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/orders?paymentStatus=FullPaymentReceived",
            TestContext.Current.CancellationToken);
        var other = await client.GetFromJsonAsync<ReportPageDto<OrdersReportItem>>(
            $"{ReportsUrl(tenant.TenantId)}/orders?paymentStatus=PaymentPending",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, matching?.Total);
        Assert.Equal(0, other?.Total);
    }

    /// <summary>
    /// Las cuatro exportaciones a Excel se retiraron: su único consumidor, <c>qep-frontend</c>,
    /// quitó la función (<c>99771b9</c>), y armaban el libro entero en memoria dentro de la
    /// petición. El llamador tiene los cuatro permisos de Reporting y un rango válido de un año,
    /// así que un 404 sólo puede venir de que la ruta ya no existe.
    /// </summary>
    [Fact]
    public async Task ExportRoutesNoLongerExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var range =
            $"from={to.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            + $"&to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        string[] reports = ["orders", "quotations", "price-changes", "customers"];

        var answered = new List<string>();
        foreach (var report in reports)
        {
            var response = await client.GetAsync(
                $"{ReportsUrl(tenant.TenantId)}/{report}/export?{range}",
                TestContext.Current.CancellationToken);
            answered.Add($"{report}: {(int)response.StatusCode}");
        }

        Assert.Equal(reports.Select(report => $"{report}: 404"), answered);
    }
}
