using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// Los dos handlers del reporte de margen comparten el orden de los demás reportes: autorizar,
/// validar, cortar el período en el día del tenant y recién entonces tocar el origen.
/// </summary>
public sealed class MarginReportHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");

    [Fact]
    public async Task WithoutTheMarginPermissionIs403BeforeValidating()
    {
        var source = new FakeMarginReportSource();
        var list = ListHandler(source, ReportingPermissions.OrdersRead);
        var summary = SummaryHandler(source, ReportingPermissions.OrdersRead);
        var filter = Filter(source: "Bogus");

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            list.HandleAsync(new ListMarginReportQuery(filter, 1, 50), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            summary.HandleAsync(new GetMarginReportSummaryQuery(filter), TestContext.Current.CancellationToken));

        Assert.Empty(source.Criteria);
    }

    [Fact]
    public async Task AnUnknownSourceIs422()
    {
        var source = new FakeMarginReportSource();
        var list = ListHandler(source, ReportingPermissions.MarginRead);
        var summary = SummaryHandler(source, ReportingPermissions.MarginRead);
        var filter = Filter(source: "Bogus");

        var listError = await Assert.ThrowsAsync<ValidationException>(() =>
            list.HandleAsync(new ListMarginReportQuery(filter, 1, 50), TestContext.Current.CancellationToken));
        var summaryError = await Assert.ThrowsAsync<ValidationException>(() =>
            summary.HandleAsync(new GetMarginReportSummaryQuery(filter), TestContext.Current.CancellationToken));

        Assert.Contains(listError.Errors, failure => failure.PropertyName == "Source");
        Assert.Contains(summaryError.Errors, failure => failure.PropertyName == "Source");
        Assert.Empty(source.Criteria);
    }

    [Fact]
    public async Task ThePeriodIsCutInTheTenantDay()
    {
        var source = new FakeMarginReportSource();
        var day = new DateOnly(2026, 12, 31);

        await ListHandler(source, ReportingPermissions.MarginRead).HandleAsync(
            new ListMarginReportQuery(Filter(day, day), 0, 5_000), TestContext.Current.CancellationToken);
        await SummaryHandler(source, ReportingPermissions.MarginRead).HandleAsync(
            new GetMarginReportSummaryQuery(Filter(day, day, "Pos")), TestContext.Current.CancellationToken);

        Assert.Equal(2, source.Criteria.Count);
        Assert.All(source.Criteria, criteria =>
        {
            Assert.Equal(new DateTimeOffset(2026, 12, 31, 5, 0, 0, TimeSpan.Zero), criteria.Period.Start);
            Assert.Equal(new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero), criteria.Period.EndExclusive);
        });
        Assert.Null(source.Criteria[0].Source);
        Assert.Equal(OrderSourceFilter.Pos, source.Criteria[1].Source);
        // El listado le pasa al origen la paginación ya normalizada.
        Assert.Equal((1, ReportPaging.MaxPageSize), source.LastPaging);
    }

    private static MarginReportFilter Filter(DateOnly? from = null, DateOnly? to = null, string? source = null) =>
        new(Tenant, from, to, source);

    private static ListMarginReportHandler ListHandler(IMarginReportSource source, params string[] permissions) =>
        new(source, new MarginReportFilterValidator(), new FakeExecutionContext(Tenant, permissions), new FixedTenantClock());

    private static GetMarginReportSummaryHandler SummaryHandler(
        IMarginReportSource source, params string[] permissions) =>
        new(source, new MarginReportFilterValidator(), new FakeExecutionContext(Tenant, permissions), new FixedTenantClock());
}

/// <summary>Un origen de margen que recuerda cada criterio con el que lo llamaron.</summary>
internal sealed class FakeMarginReportSource : IMarginReportSource
{
    public List<MarginReportCriteria> Criteria { get; } = [];

    public (int Page, int PageSize)? LastPaging { get; private set; }

    public Task<(IReadOnlyList<MarginReportProductDto> Items, int Total)> ListAsync(
        MarginReportCriteria criteria,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        Criteria.Add(criteria);
        LastPaging = (page, pageSize);
        return Task.FromResult(((IReadOnlyList<MarginReportProductDto>)[], 0));
    }

    public Task<IReadOnlyList<MarginReportCurrencyDto>> SummarizeAsync(
        MarginReportCriteria criteria,
        CancellationToken ct)
    {
        Criteria.Add(criteria);
        return Task.FromResult<IReadOnlyList<MarginReportCurrencyDto>>([]);
    }
}
