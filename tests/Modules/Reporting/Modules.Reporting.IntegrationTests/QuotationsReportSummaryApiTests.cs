using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El resumen agregado de cotizaciones: <c>GET /reports/quotations/summary</c>.
///
/// Igual que el de ventas, estas pruebas existen sobre todo para verificar **que los agregados se
/// traduzcan a SQL**, y acá hay uno que el de ventas no tiene: los tramos de vigencia agrupan por
/// una cadena de condicionales sobre <c>ValidUntil</c>. Eso compila siempre y traduce sólo si EF
/// sabe convertir la expresión en un <c>CASE</c> — es exactamente el tipo de consulta que revienta
/// en runtime y ninguna prueba unitaria alcanza a ver.
/// </summary>
public sealed class QuotationsReportSummaryApiTests
{
    [Fact]
    public async Task SummaryAddsUpTheTenantsQuotationsAndClassifiesThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<QuotationsReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);

        Assert.Equal(1, summary.QuotationCount);
        Assert.Equal(quotation.Subtotal, summary.Subtotal);
        Assert.Equal(quotation.TaxAmount, summary.TaxAmount);
        Assert.Equal(quotation.Total, summary.Total);

        var month = Assert.Single(summary.Monthly);
        Assert.Equal(1, month.Count);
        Assert.Equal(quotation.Total, month.Total);

        // Los cuatro estados vienen siempre, incluso en cero: la pantalla no tiene que saber
        // cuáles existen para dibujar el que falta.
        Assert.Equal(4, summary.ByStatus.Count);
        var sent = Assert.Single(summary.ByStatus, slice => slice.Status == "Sent");
        Assert.Equal(1, sent.Count);
        Assert.Equal(quotation.Total, sent.Total);
        Assert.All(
            summary.ByStatus.Where(slice => slice.Status != "Sent"),
            slice => Assert.Equal(0, slice.Count));
        // Y ninguno es "Approved": convertir deja la cotización en Sent.
        Assert.DoesNotContain(summary.ByStatus, slice => slice.Status == "Approved");

        var advisor = Assert.Single(summary.ByAdvisor);
        Assert.Equal(tenant.OwnerEmail, advisor.Label);
        Assert.Equal(1, advisor.Count);

        Assert.Null(summary.Previous);
    }

    /// <summary>
    /// Los tramos de vigencia, que es el agregado que más puede no traducirse.
    ///
    /// No se afirma **en qué** tramo cae la cotización sembrada —el <c>ValidUntil</c> lo decide el
    /// dominio al enviarla, y atarlo acá haría que la prueba se rompa el día que esa regla cambie
    /// por un motivo ajeno a este reporte—. Lo que sí se afirma es que los cinco contadores cierran
    /// contra las enviadas: si un tramo se perdiera o se contara dos veces, esto lo ve.
    /// </summary>
    [Fact]
    public async Task ValidityBucketsAccountForEverySentQuotation()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        await CreateSentQuotationAsync(client, factory, tenant.TenantId, customer.Id, productId);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<QuotationsReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);

        var validity = summary.Validity;
        var classified =
            validity.Expired.Count
            + validity.WithinSevenDays.Count
            + validity.WithinThirtyDays.Count
            + validity.Beyond.Count
            + validity.WithoutExpiry;
        var sentCount = summary.ByStatus.Single(slice => slice.Status == "Sent").Count;
        Assert.Equal(sentCount, classified);

        // La cola nunca trae algo ya vencido: eso vive en el tramo Expired, no acá.
        Assert.All(summary.Expiring, entry => Assert.True(entry.DaysLeft >= 0));
    }

    [Fact]
    public async Task SummaryReturnsZerosWhenTheTenantHasNoQuotations()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<QuotationsReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(0, summary.QuotationCount);
        Assert.Equal(0m, summary.Total);
        Assert.Empty(summary.Monthly);
        Assert.Empty(summary.ByAdvisor);
        Assert.Empty(summary.Expiring);
        // Los cuatro estados en cero, no una lista vacía: ver la prueba de arriba.
        Assert.Equal(4, summary.ByStatus.Count);
        Assert.All(summary.ByStatus, slice => Assert.Equal(0, slice.Count));
        Assert.Equal(0, summary.Validity.WithoutExpiry);
    }

    [Fact]
    public async Task SummaryWithADateRangeCarriesTheComparisonAgainstTheWindowBefore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        await CreateSentQuotationAsync(client, factory, tenant.TenantId, customer.Id, productId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-29);

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary"
                + $"?from={from:yyyy-MM-dd}&to={today:yyyy-MM-dd}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<QuotationsReportSummary>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.QuotationCount);

        Assert.NotNull(summary.Previous);
        Assert.Equal(0, summary.Previous.Count);
        Assert.Equal(0m, summary.Previous.Total);
    }

    [Fact]
    public async Task SummaryRejectsAnotherTenantsReport()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(Guid.CreateVersion7())}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(
            TestContext.Current.CancellationToken);
        Assert.Equal("authorization.denied", problem?.Code);
    }

    [Fact]
    public async Task SummaryRejectsACallerWithoutTheReportingPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, SeedOnlyPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>«Approved» no es un estado de cotización: es 422 con el mapa <c>errors</c>, no un
    /// resultado vacío que se leería como "no hubo ninguna".</summary>
    [Fact]
    public async Task SummaryRejectsAStatusThatDoesNotExist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;

        var response = await client.GetAsync(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary?status=Approved",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
