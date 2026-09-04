using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El handler del resumen de cotizaciones.
///
/// Comparte con el de ventas el orden no negociable —autorizar, validar, recien despues tocar el
/// origen— y la regla de la ventana anterior. Lo suyo es el reloj: los tramos de vigencia y la
/// cola de vencimientos dependen de que dia es hoy, y el handler es quien resuelve ese "hoy" para
/// que el origen no consulte ningun reloj.
/// </summary>
public sealed class QuotationsReportSummaryHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Advisor = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    /// <summary>Media tarde en UTC: si el handler tomara la fecha local en vez de la UTC, en un
    /// huso al oeste esto seria todavia el dia anterior.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task SummarizingRejectsATenantThatIsNotTheCallersOne()
    {
        var source = new FakeQuotationsReportSource();
        var handler = Handler(source, OtherTenant, ReportingPermissions.QuotationRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetQuotationsReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>El permiso es el de cotizaciones, no el de ventas: tenerlos separados es lo unico
    /// que hace que un asesor pueda ver uno y no el otro.</summary>
    [Fact]
    public async Task SummarizingRejectsACallerWithOnlyTheSalesPermission()
    {
        var source = new FakeQuotationsReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetQuotationsReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    [Fact]
    public async Task SummarizingRejectsAnUnknownStatus()
    {
        var source = new FakeQuotationsReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                new GetQuotationsReportSummaryQuery(Filter(status: "Approved")),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Status");
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>«Aprobada» no existe: convertir una cotizacion la deja en <c>Sent</c>. Que el
    /// validador la rechace es lo que evita que alguien filtre por un estado inventado y lea un
    /// cero como "no hubo".</summary>
    [Fact]
    public async Task SummarizingAcceptsTheFourRealStatuses()
    {
        foreach (var status in new[] { "Draft", "Sent", "Expired", "Voided" })
        {
            var source = new FakeQuotationsReportSource();
            var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

            await handler.HandleAsync(
                new GetQuotationsReportSummaryQuery(Filter(status: status)),
                TestContext.Current.CancellationToken);

            Assert.Single(source.SummarizedCriteria);
        }
    }

    /// <summary>El "hoy" que resuelve los tramos sale del reloj inyectado y en UTC — no de
    /// <c>DateTime.Today</c>, que dependeria del huso de la maquina que corre la API.</summary>
    [Fact]
    public async Task SummarizingResolvesTodayFromTheClockInUtc()
    {
        var source = new FakeQuotationsReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        var options = Assert.Single(source.SummarizedOptions);
        Assert.Equal(new DateOnly(2026, 9, 3), options.Today);
        Assert.Equal(ReportSummaryRules.RankSize, options.RankSize);
        Assert.Equal(ReportSummaryRules.ExpiringWithinDays, options.ExpiringWithinDays);
        Assert.Equal(ReportSummaryRules.ExpiringSize, options.ExpiringSize);
    }

    [Fact]
    public async Task SummarizingWithoutADateRangeAsksTheSourceOnceAndHasNoComparison()
    {
        var source = new FakeQuotationsReportSource
        {
            Aggregate = FakeQuotationsReportSource.EmptyAggregate(
                quotationCount: 9, total: 900m),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        var summary = await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        Assert.Single(source.SummarizedCriteria);
        Assert.Null(summary.Previous);
        Assert.Equal(9, summary.QuotationCount);
        Assert.Equal(900m, summary.Total);
    }

    [Fact]
    public async Task SummarizingWithADateRangeComparesAgainstTheWindowImmediatelyBefore()
    {
        var source = new FakeQuotationsReportSource
        {
            Aggregate = FakeQuotationsReportSource.EmptyAggregate(30, 3_000m),
            PrecedingAggregate = FakeQuotationsReportSource.EmptyAggregate(20, 2_500m),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        var summary = await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(
                Filter(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.SummarizedCriteria.Count);
        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(new DateOnly(2025, 12, 1), preceding.From);
        Assert.Equal(new DateOnly(2025, 12, 31), preceding.To);

        Assert.NotNull(summary.Previous);
        Assert.Equal(20, summary.Previous.Count);
        Assert.Equal(2_500m, summary.Previous.Total);
    }

    [Fact]
    public async Task TheComparisonKeepsEveryOtherFilter()
    {
        var source = new FakeQuotationsReportSource
        {
            PrecedingAggregate = FakeQuotationsReportSource.EmptyAggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(Filter(
                from: new DateOnly(2026, 1, 1),
                to: new DateOnly(2026, 1, 31),
                advisorId: Advisor,
                status: "Sent")),
            TestContext.Current.CancellationToken);

        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(Tenant, preceding.TenantId);
        Assert.Equal(Advisor, preceding.AdvisorId);
        Assert.Equal(QuotationStatusFilter.Sent, preceding.Status);
    }

    /// <summary>
    /// De la ventana anterior sólo se leen el conteo y el monto, así que no se le pide ni el
    /// ranking ni la cola de vencimientos: son cuatro consultas que nadie mira, y la cola de un
    /// periodo que ya paso no significa nada — "vence en 3 dias" medido contra hoy, sobre
    /// cotizaciones de hace dos meses.
    /// </summary>
    [Fact]
    public async Task TheComparisonDoesNotAskForRankingsOrTheExpiringQueue()
    {
        var source = new FakeQuotationsReportSource
        {
            PrecedingAggregate = FakeQuotationsReportSource.EmptyAggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.QuotationRead);

        await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(
                Filter(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        var preceding = source.SummarizedOptions[1];
        Assert.Equal(0, preceding.RankSize);
        Assert.Equal(0, preceding.ExpiringSize);
        // El "hoy" no cambia entre las dos: es el mismo instante de ejecucion.
        Assert.Equal(source.SummarizedOptions[0].Today, preceding.Today);
    }

    private static GetQuotationsReportSummaryHandler Handler(
        IQuotationsReportSource source,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            new QuotationsReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedClock(Now));

    private static QuotationsReportFilter Filter(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        string? status = null) =>
        new(Tenant, from, to, advisorId, ClientId: null, status);
}
