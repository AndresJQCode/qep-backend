using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El handler del resumen de pedidos.
///
/// Comparte con los ocho de <see cref="OrdersReportHandlerTests"/> el orden no negociable
/// —autorizar, validar, recien despues tocar el origen— y agrega lo suyo: que la comparacion
/// contra el periodo anterior sea **una segunda consulta con los mismos filtros y otra ventana**,
/// y que sin rango de fechas simplemente no exista.
/// </summary>
public sealed class OrdersReportSummaryHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Advisor = Guid.Parse("01900000-0000-7000-8000-0000000000a1");

    [Fact]
    public async Task SummarizingRejectsATenantThatIsNotTheCallersOne()
    {
        var source = new FakeOrdersReportSource();
        var handler = Handler(source, OtherTenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetOrdersReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        // Autorizar va primero: el origen no se toco.
        Assert.Empty(source.SummarizedCriteria);
    }

    [Fact]
    public async Task SummarizingRejectsACallerWithoutThePermission()
    {
        var source = new FakeOrdersReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.CustomerRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetOrdersReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>Mismo 422 con mapa <c>errors</c> que el listado: el resumen no afloja la
    /// validacion porque comparte el filtro y su validador.</summary>
    [Fact]
    public async Task SummarizingRejectsAnUnknownPaymentStatus()
    {
        var source = new FakeOrdersReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                new GetOrdersReportSummaryQuery(Filter(paymentStatus: "Refunded")),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "PaymentStatus");
        Assert.Empty(source.SummarizedCriteria);
    }

    [Fact]
    public async Task SummarizingParsesThePaymentStatusFilter()
    {
        var source = new FakeOrdersReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(Filter(paymentStatus: "PartialPaymentReceived")),
            TestContext.Current.CancellationToken);

        var criteria = Assert.Single(source.SummarizedCriteria);
        Assert.Equal(OrderPaymentStatusFilter.PartialPaymentReceived, criteria.PaymentStatus);
    }

    /// <summary>
    /// Sin las dos puntas del rango no hay periodo anterior, asi que hay **una sola** consulta y
    /// <c>Previous</c> viene nulo. Es lo que le permite al frontend no dibujar el delta en vez de
    /// dibujar un "+0%" que nadie calculo.
    /// </summary>
    [Fact]
    public async Task SummarizingWithoutADateRangeAsksTheSourceOnceAndHasNoComparison()
    {
        var source = new FakeOrdersReportSource
        {
            Aggregate = Aggregate(orderCount: 12, total: 1_200m),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        var summary = await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(Filter()), TestContext.Current.CancellationToken);

        Assert.Single(source.SummarizedCriteria);
        Assert.Null(summary.Previous);
        Assert.Equal(12, summary.SaleCount);
        Assert.Equal(1_200m, summary.Total);
    }

    /// <summary>
    /// Con rango, la segunda consulta es la misma ventana corrida hacia atras: enero contra
    /// diciembre. Que sea el origen quien la resuelva y no el handler quien reste importes es lo
    /// que hace que el delta sea del periodo y no de la pagina.
    /// </summary>
    [Fact]
    public async Task SummarizingWithADateRangeComparesAgainstTheWindowImmediatelyBefore()
    {
        var source = new FakeOrdersReportSource
        {
            Aggregate = Aggregate(orderCount: 30, total: 3_000m),
            PrecedingAggregate = Aggregate(orderCount: 20, total: 2_500m),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        var summary = await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(
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

    /// <summary>
    /// El periodo anterior conserva **todos** los demas filtros. Comparar "enero del asesor X"
    /// contra "diciembre de todos" seria un delta inventado, y es un error facil de cometer al
    /// armar el segundo criterio a mano.
    /// </summary>
    [Fact]
    public async Task TheComparisonKeepsEveryOtherFilter()
    {
        var source = new FakeOrdersReportSource
        {
            Aggregate = Aggregate(),
            PrecedingAggregate = Aggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(Filter(
                from: new DateOnly(2026, 1, 1),
                to: new DateOnly(2026, 1, 31),
                advisorId: Advisor,
                paymentStatus: "PaymentPending")),
            TestContext.Current.CancellationToken);

        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(Tenant, preceding.TenantId);
        Assert.Equal(Advisor, preceding.AdvisorId);
        Assert.Equal(OrderPaymentStatusFilter.PaymentPending, preceding.PaymentStatus);
    }

    /// <summary>El tamaño del ranking lo fija el handler, no el llamador: sin tope, un tenant con
    /// miles de clientes devolveria miles de filas en un resumen.</summary>
    [Fact]
    public async Task SummarizingCapsTheRankingItAsksFor()
    {
        var source = new FakeOrdersReportSource { Aggregate = Aggregate() };
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(Filter()), TestContext.Current.CancellationToken);

        Assert.Equal(ReportSummaryRules.RankSize, source.LastRankSize);
    }

    private static GetOrdersReportSummaryHandler Handler(
        IOrdersReportSource source,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));

    private static OrdersReportFilter Filter(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? advisorId = null,
        string? paymentStatus = null) =>
        new(Tenant, from, to, advisorId, ClientId: null, paymentStatus);

    private static OrdersReportAggregate Aggregate(int orderCount = 0, decimal total = 0m) =>
        new(orderCount, total, 0m, total, [], [], []);
}
