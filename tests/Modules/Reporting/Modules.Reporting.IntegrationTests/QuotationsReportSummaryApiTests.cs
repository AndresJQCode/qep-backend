using System.Net;
using System.Net.Http.Json;
using static Modules.Reporting.IntegrationTests.ReportingApiHarness;

namespace Modules.Reporting.IntegrationTests;

/// <summary>
/// El resumen agregado de cotizaciones: <c>GET /reports/quotations/summary</c>.
///
/// Igual que el de pedidos, estas pruebas existen sobre todo para verificar **que los agregados se
/// traduzcan a SQL**, y acá hay uno que el de pedidos no tiene: los tramos de vigencia agrupan por
/// una cadena de condicionales sobre <c>ValidUntil</c>. Eso compila siempre y traduce sólo si EF
/// sabe convertir la expresión en un <c>CASE</c> — es exactamente el tipo de consulta que revienta
/// en runtime y ninguna prueba unitaria alcanza a ver.
/// </summary>
public sealed class QuotationsReportSummaryApiTests
{
    // Spec 2026-09-17, punto 6: el 31 de diciembre a las 23:00 en Bogotá, la cotización que vence el
    // 31 vence hoy —por vencer, cero días— y no cuenta como vencida.
    [Fact]
    public async Task AQuotationDueOnTheTenantsTodayIsNotExpired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId, validUntil: new DateOnly(2026, 12, 31));

        var summary = await client.GetFromJsonAsync<QuotationsReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Validity.Expired.Count);
        Assert.Equal(1, summary.Validity.WithinSevenDays.Count);
        var expiring = Assert.Single(summary.Expiring);
        Assert.Equal(quotation.Id, expiring.QuotationId);
        Assert.Equal(0, expiring.DaysLeft);
    }

    /// <summary>
    /// Review Focus 5 and plan decision A10. A tenant with COP and EUR quotations gets one amount
    /// per currency in every money figure of the summary, never a sum across them. The expiring
    /// queue puts the default-currency (COP) quotation first, even though the EUR one has the
    /// numerically larger total.
    /// </summary>
    [Fact]
    public async Task CopAndEurQuotationsComeBackAsSeparateTotalsAndTheQueueLeadsWithTheDefaultCurrency()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var copProduct = await CreateProductAsync(client, tenant.TenantId);
        var eurProduct = await CreateProductAsync(
            client, tenant.TenantId, prices: new Dictionary<string, decimal> { ["EUR"] = 900_000m });
        // Three days out: inside the seven-day bucket and the expiring queue, away from both edges.
        var dueSoon = TodayInBogota().AddDays(3);
        var inPesos = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, copProduct, validUntil: dueSoon);
        var inEuros = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, eurProduct, validUntil: dueSoon, currency: "EUR");

        var summary = await client.GetFromJsonAsync<QuotationsReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary", TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        MoneyAmount[] totals = [new MoneyAmount("COP", inPesos.Total), new MoneyAmount("EUR", inEuros.Total)];
        Assert.Equal(2, summary.QuotationCount);
        Assert.Equal(
            [new MoneyAmount("COP", inPesos.Subtotal), new MoneyAmount("EUR", inEuros.Subtotal)],
            summary.Subtotals);
        Assert.Equal(
            [new MoneyAmount("COP", inPesos.TaxAmount), new MoneyAmount("EUR", inEuros.TaxAmount)],
            summary.TaxAmounts);
        Assert.Equal(totals, summary.Totals);
        Assert.Equal(totals, Assert.Single(summary.Monthly).Totals);
        Assert.Equal(totals, Assert.Single(summary.ByAdvisor).Totals);

        var sent = Assert.Single(summary.ByStatus, slice => slice.Status == "Sent");
        Assert.Equal(2, sent.Count);
        Assert.Equal(totals, sent.Totals);
        // A status with no quotations is a zero count and no money, not a zero in some currency.
        var draft = Assert.Single(summary.ByStatus, slice => slice.Status == "Draft");
        Assert.Equal(0, draft.Count);
        Assert.Empty(draft.Totals);

        Assert.Equal(2, summary.Validity.WithinSevenDays.Count);
        Assert.Equal(totals, summary.Validity.WithinSevenDays.Totals);
        Assert.Empty(summary.Validity.Beyond.Totals);

        Assert.Equal([inPesos.Id, inEuros.Id], summary.Expiring.Select(entry => entry.QuotationId));
        Assert.Equal(["COP", "EUR"], summary.Expiring.Select(entry => entry.Currency));
        Assert.Equal(inEuros.Total, summary.Expiring[1].Total);
    }

    // Spec 2026-09-17, punto 5: la cotización creada el 31 de diciembre a las 23:00 de Bogotá —ya
    // enero en UTC— cuenta en la serie de diciembre del tenant.
    [Fact]
    public async Task TheMonthlySeriesGroupsByTheTenantsLocalMonth()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), NewYearsEveInBogota);
        var tenant = await RegisterTenantAsync(factory, ManagerPermissions);
        using var client = tenant.Client;
        var customer = await CreateActiveCustomerAsync(client, tenant.TenantId);
        var productId = await CreateProductAsync(client, tenant.TenantId);
        var quotation = await CreateSentQuotationAsync(
            client, factory, tenant.TenantId, customer.Id, productId);

        var summary = await client.GetFromJsonAsync<QuotationsReportSummary>(
            $"{ReportsUrl(tenant.TenantId)}/quotations/summary?from=2026-12-01&to=2026-12-31",
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.QuotationCount);
        var month = Assert.Single(summary.Monthly);
        Assert.Equal((2026, 12), (month.Year, month.Month));
        Assert.Equal([new MoneyAmount("COP", quotation.Total)], month.Totals);
    }

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
        Assert.Equal([new MoneyAmount("COP", quotation.Subtotal)], summary.Subtotals);
        Assert.Equal([new MoneyAmount("COP", quotation.TaxAmount)], summary.TaxAmounts);
        Assert.Equal([new MoneyAmount("COP", quotation.Total)], summary.Totals);

        var month = Assert.Single(summary.Monthly);
        Assert.Equal(1, month.Count);
        Assert.Equal([new MoneyAmount("COP", quotation.Total)], month.Totals);

        // Los cinco estados vienen siempre, incluso en cero: la pantalla no tiene que saber
        // cuáles existen para dibujar el que falta.
        Assert.Equal(5, summary.ByStatus.Count);
        var sent = Assert.Single(summary.ByStatus, slice => slice.Status == "Sent");
        Assert.Equal(1, sent.Count);
        Assert.Equal([new MoneyAmount("COP", quotation.Total)], sent.Totals);
        Assert.All(
            summary.ByStatus.Where(slice => slice.Status != "Sent"),
            slice => Assert.Equal(0, slice.Count));
        // Y ninguno es "Approved": ése es un estado del pedido. Convertir deja la cotización en
        // Converted.
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
        Assert.Empty(summary.Totals);
        Assert.Empty(summary.Monthly);
        Assert.Empty(summary.ByAdvisor);
        Assert.Empty(summary.Expiring);
        // Los cinco estados en cero, no una lista vacía: ver la prueba de arriba.
        Assert.Equal(5, summary.ByStatus.Count);
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

        var today = TodayInBogota();
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
        Assert.Empty(summary.Previous.Totals);
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
