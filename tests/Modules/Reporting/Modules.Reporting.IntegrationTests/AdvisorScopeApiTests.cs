using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El alcance por asesor de los reportes de pedidos y cotizaciones (decisión 2026-09-24): sin
/// <c>reporting.all_advisors.read</c> sólo se ve lo propio, aunque la URL pida a otro asesor.
///
/// Las unitarias ya prueban que el handler reemplace el filtro. Esto prueba lo que ellas no
/// alcanzan: que el id propio que resuelve el directorio de membresías sea **el mismo** que la
/// cotización graba como asesor, contra la base real — si fueran ids distintos (usuario contra
/// membresía), el acotamiento dejaría a la asesora viendo cero filas y ninguna unitaria lo notaría.
/// </summary>
public sealed class AdvisorScopeApiTests
{
    [Fact]
    public async Task WithoutAllAdvisorsTheReportsOnlyShowTheCallersOwnRowsAndAManagerSeesBoth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var manager = tenant.Client;
        var customer = await CreateActiveCustomerAsync(manager, tenant.TenantId);
        var productId = await CreateProductAsync(manager, tenant.TenantId);

        // Asesor A es el dueño del tenant; asesora B entra por invitación. Cada uno cotiza y
        // convierte en pedido lo suyo.
        var advisorB = await InviteActiveAdvisorAsync(
            factory, database, tenant, OwnAdvisorPermissions);
        using var clientB = advisorB.Client;
        var quotationA = await CreateSentQuotationAsync(
            manager, factory, tenant.TenantId, customer.Id, productId);
        var orderA = await ConvertToOrderAsync(manager, factory, tenant.TenantId, quotationA);
        var quotationB = await CreateSentQuotationAsync(
            clientB, factory, tenant.TenantId, customer.Id, productId);
        var orderB = await ConvertToOrderAsync(clientB, factory, tenant.TenantId, quotationB);

        // A sin el permiso de ver a todos, pidiendo explícitamente los datos de B.
        using var scopedA = CreateClient(
            factory,
            tenant.OwnerUserId.ToString(),
            tenant.TenantId.ToString(),
            OwnAdvisorPermissions);
        var askForB = $"advisorId={advisorB.MembershipId}";

        var quotations = await GetAsync<ReportPageDto<QuotationsReportItem>>(
            scopedA, $"{ReportsUrl(tenant.TenantId)}/quotations?{askForB}");
        Assert.Equal(quotationA.Id, Assert.Single(quotations.Items).QuotationId);

        var orders = await GetAsync<ReportPageDto<OrdersReportItem>>(
            scopedA, $"{ReportsUrl(tenant.TenantId)}/orders?{askForB}");
        Assert.Equal(orderA.Id, Assert.Single(orders.Items).OrderId);

        var quotationsSummary = await GetAsync<QuotationsReportSummary>(
            scopedA, $"{ReportsUrl(tenant.TenantId)}/quotations/summary?{askForB}");
        Assert.Equal(1, quotationsSummary.QuotationCount);
        Assert.Equal(tenant.OwnerEmail, Assert.Single(quotationsSummary.ByAdvisor).Label);

        var ordersSummary = await GetAsync<OrdersReportSummary>(
            scopedA, $"{ReportsUrl(tenant.TenantId)}/orders/summary?{askForB}");
        Assert.Equal(1, ordersSummary.OrderCount);
        Assert.Equal(tenant.OwnerEmail, Assert.Single(ordersSummary.ByAdvisor).Label);

        // B, sin filtro, ve sólo lo suyo: el alcance no depende de que el cliente mande advisorId.
        var ordersOfB = await GetAsync<ReportPageDto<OrdersReportItem>>(
            clientB, $"{ReportsUrl(tenant.TenantId)}/orders");
        Assert.Equal(orderB.Id, Assert.Single(ordersOfB.Items).OrderId);

        // Con el permiso: sin filtro ve a los dos, y el advisorId que pide se respeta.
        var everyQuotation = await GetAsync<ReportPageDto<QuotationsReportItem>>(
            manager, $"{ReportsUrl(tenant.TenantId)}/quotations");
        Assert.Equal(
            new[] { quotationA.Id, quotationB.Id }.Order(),
            everyQuotation.Items.Select(item => item.QuotationId).Order());

        var everyOrder = await GetAsync<OrdersReportSummary>(
            manager, $"{ReportsUrl(tenant.TenantId)}/orders/summary");
        Assert.Equal(2, everyOrder.OrderCount);
        Assert.Equal(2, everyOrder.ByAdvisor.Count);

        var onlyB = await GetAsync<ReportPageDto<OrdersReportItem>>(
            manager, $"{ReportsUrl(tenant.TenantId)}/orders?{askForB}");
        Assert.Equal(orderB.Id, Assert.Single(onlyB.Items).OrderId);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var body = await client.GetFromJsonAsync<T>(url, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }
}
