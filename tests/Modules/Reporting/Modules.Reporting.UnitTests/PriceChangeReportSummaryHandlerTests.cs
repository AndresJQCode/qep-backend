using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El handler del resumen de cambios de precio.
///
/// Comparte con sus dos hermanos el orden no negociable —autorizar, validar, recién después tocar
/// el origen— y la regla de la ventana anterior. Lo suyo es lo que **no** tiene: ningún agregado
/// de monto. Los valores del histórico conviven en dólares, pesos y puntos de descuento, así que
/// un total o un promedio sería la suma de tres unidades distintas; lo único que se puede sumar
/// sin mentir es cuántos cambios hubo y hacia dónde fueron.
/// </summary>
public sealed class PriceChangeReportSummaryHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Product = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid Author = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public async Task SummarizingRejectsATenantThatIsNotTheCallersOne()
    {
        var source = new FakePriceChangeReportSource();
        var handler = Handler(source, OtherTenant, ReportingPermissions.PriceChangeRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetPriceChangeReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>El reporte de cambios de precio es sólo del Administrador: el permiso de ventas
    /// —que sí tiene un asesor— no alcanza para verlo.</summary>
    [Fact]
    public async Task SummarizingRejectsACallerWithOnlyTheSalesPermission()
    {
        var source = new FakePriceChangeReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetPriceChangeReportSummaryQuery(Filter()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    [Fact]
    public async Task SummarizingRejectsAnUnknownField()
    {
        var source = new FakePriceChangeReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                new GetPriceChangeReportSummaryQuery(Filter(field: "PriceFinalCop")),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "Field");
        Assert.Empty(source.SummarizedCriteria);
    }

    /// <summary>Los tres campos del histórico, y no más: el resumen filtra exactamente por lo
    /// mismo que el listado.</summary>
    [Fact]
    public async Task SummarizingAcceptsTheThreeRealFields()
    {
        foreach (var field in new[] { "PriceBaseUsd", "PriceBaseCop", "ScaleDiscount" })
        {
            var source = new FakePriceChangeReportSource();
            var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

            await handler.HandleAsync(
                new GetPriceChangeReportSummaryQuery(Filter(field: field)),
                TestContext.Current.CancellationToken);

            Assert.Single(source.SummarizedCriteria);
        }
    }

    [Fact]
    public async Task SummarizingWithoutADateRangeAsksTheSourceOnceAndHasNoComparison()
    {
        var source = new FakePriceChangeReportSource
        {
            Aggregate = FakePriceChangeReportSource.EmptyAggregate(
                changeCount: 148, increaseCount: 96, decreaseCount: 52),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

        var summary = await handler.HandleAsync(
            new GetPriceChangeReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        Assert.Single(source.SummarizedCriteria);
        Assert.Null(summary.Previous);
        Assert.Equal(148, summary.ChangeCount);
        Assert.Equal(96, summary.IncreaseCount);
        Assert.Equal(52, summary.DecreaseCount);
    }

    [Fact]
    public async Task SummarizingAsksForTheRankOfProducts()
    {
        var source = new FakePriceChangeReportSource();
        var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

        await handler.HandleAsync(
            new GetPriceChangeReportSummaryQuery(Filter()),
            TestContext.Current.CancellationToken);

        Assert.Equal(ReportSummaryRules.RankSize, Assert.Single(source.SummarizedRankSizes));
    }

    [Fact]
    public async Task SummarizingWithADateRangeComparesAgainstTheWindowImmediatelyBefore()
    {
        var source = new FakePriceChangeReportSource
        {
            Aggregate = FakePriceChangeReportSource.EmptyAggregate(changeCount: 148),
            PrecedingAggregate = FakePriceChangeReportSource.EmptyAggregate(changeCount: 121),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

        var summary = await handler.HandleAsync(
            new GetPriceChangeReportSummaryQuery(
                Filter(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.SummarizedCriteria.Count);
        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(new DateOnly(2025, 12, 1), preceding.From);
        Assert.Equal(new DateOnly(2025, 12, 31), preceding.To);

        Assert.NotNull(summary.Previous);
        Assert.Equal(121, summary.Previous.ChangeCount);
    }

    [Fact]
    public async Task TheComparisonKeepsEveryOtherFilter()
    {
        var source = new FakePriceChangeReportSource
        {
            PrecedingAggregate = FakePriceChangeReportSource.EmptyAggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

        await handler.HandleAsync(
            new GetPriceChangeReportSummaryQuery(Filter(
                from: new DateOnly(2026, 1, 1),
                to: new DateOnly(2026, 1, 31),
                productId: Product,
                changedBy: Author,
                field: "ScaleDiscount")),
            TestContext.Current.CancellationToken);

        var preceding = source.SummarizedCriteria[1];
        Assert.Equal(Tenant, preceding.TenantId);
        Assert.Equal(Product, preceding.ProductId);
        Assert.Equal(Author, preceding.ChangedBy);
        Assert.Equal(PriceChangeField.ScaleDiscount, preceding.Field);
    }

    /// <summary>De la ventana anterior sólo se lee el conteo, así que no se le pide el ranking:
    /// son consultas que nadie mira, y "los productos más retocados del periodo anterior" no
    /// aparece en ninguna pantalla.</summary>
    [Fact]
    public async Task TheComparisonDoesNotAskForTheRanking()
    {
        var source = new FakePriceChangeReportSource
        {
            PrecedingAggregate = FakePriceChangeReportSource.EmptyAggregate(),
        };
        var handler = Handler(source, Tenant, ReportingPermissions.PriceChangeRead);

        await handler.HandleAsync(
            new GetPriceChangeReportSummaryQuery(
                Filter(from: new DateOnly(2026, 1, 1), to: new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, source.SummarizedRankSizes[1]);
    }

    private static GetPriceChangeReportSummaryHandler Handler(
        IPriceChangeReportSource source,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            new PriceChangeReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));

    private static PriceChangeReportFilter Filter(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? productId = null,
        Guid? changedBy = null,
        string? field = null) =>
        new(Tenant, from, to, productId, changedBy, field);
}
