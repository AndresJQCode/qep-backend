using BuildingBlocks.Application;
using Modules.Reporting.Application;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El alcance por asesor de los reportes de pedidos y de cotizaciones (decisión con el dueño del
/// producto, 2026-09-24): quien no tiene <see cref="ReportingPermissions.AllAdvisorsRead"/> sólo ve
/// lo suyo, mande el <c>advisorId</c> que mande.
///
/// Se prueba en los cuatro handlers y no en uno representante, porque lo que importa es justo que
/// ninguno se haya quedado sin el acotamiento: un listado acotado con un resumen que no lo está
/// deja ver los totales de los demás por la puerta de al lado.
/// </summary>
public sealed class AdvisorScopeHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherAdvisor = Guid.Parse("01900000-0000-7000-8000-0000000000b2");
    private static readonly Guid Caller = FakeMembershipDirectory.CallerMembershipId;

    // ---- Listado de pedidos ----

    [Fact]
    public async Task OrdersListingWithoutAllAdvisorsIgnoresTheRequestedAdvisorAndUsesTheCallers()
    {
        var source = new FakeOrdersReportSource();
        var directory = new FakeMembershipDirectory();
        var context = new FakeExecutionContext(Tenant, ReportingPermissions.OrdersRead);
        var handler = OrdersList(source, context, directory);

        await handler.HandleAsync(
            new ListOrdersReportQuery(OrdersFilter(OtherAdvisor), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(Caller, source.LastCriteria?.AdvisorId);
        // El asesor propio sale de la membresía activa de quien llama en el tenant de la ruta.
        Assert.Equal((context.SubjectId, Tenant), Assert.Single(directory.Lookups));
    }

    [Fact]
    public async Task OrdersListingWithoutAllAdvisorsAndNoAdvisorScopesToTheCaller()
    {
        var source = new FakeOrdersReportSource();
        var handler = OrdersList(
            source,
            new FakeExecutionContext(Tenant, ReportingPermissions.OrdersRead),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new ListOrdersReportQuery(OrdersFilter(advisorId: null), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(Caller, source.LastCriteria?.AdvisorId);
    }

    [Fact]
    public async Task OrdersListingWithAllAdvisorsPassesTheRequestedAdvisorThrough()
    {
        var source = new FakeOrdersReportSource();
        var directory = new FakeMembershipDirectory();
        var handler = OrdersList(
            source,
            new FakeExecutionContext(
                Tenant, ReportingPermissions.OrdersRead, ReportingPermissions.AllAdvisorsRead),
            directory);

        await handler.HandleAsync(
            new ListOrdersReportQuery(OrdersFilter(OtherAdvisor), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(OtherAdvisor, source.LastCriteria?.AdvisorId);
        Assert.Empty(directory.Lookups);
    }

    [Fact]
    public async Task OrdersListingWithAllAdvisorsAndNoAdvisorKeepsEveryAdvisor()
    {
        var source = new FakeOrdersReportSource();
        var handler = OrdersList(
            source,
            new FakeExecutionContext(
                Tenant, ReportingPermissions.OrdersRead, ReportingPermissions.AllAdvisorsRead),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new ListOrdersReportQuery(OrdersFilter(advisorId: null), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.NotNull(source.LastCriteria);
        Assert.Null(source.LastCriteria.AdvisorId);
    }

    /// <summary>Sin membresía activa no hay asesor al cual acotar, y la salida segura es 403: ni
    /// los datos de otro ni un listado "vacío" que en realidad no filtró nada.</summary>
    [Fact]
    public async Task OrdersListingWithoutAllAdvisorsAndWithoutAnActiveMembershipIsForbidden()
    {
        var source = new FakeOrdersReportSource();
        var handler = OrdersList(
            source,
            new FakeExecutionContext(Tenant, ReportingPermissions.OrdersRead),
            new FakeMembershipDirectory(activeMembershipId: null));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ListOrdersReportQuery(OrdersFilter(OtherAdvisor), 1, 50),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Null(source.LastCriteria);
    }

    // ---- Resumen de pedidos ----

    /// <summary>La ventana anterior es una segunda consulta: si sólo la primera quedara acotada,
    /// el delta compararía "mis pedidos" contra "los de todos".</summary>
    [Fact]
    public async Task OrdersSummaryWithoutAllAdvisorsScopesBothTheRangeAndThePrecedingWindow()
    {
        var source = new FakeOrdersReportSource();
        var handler = new GetOrdersReportSummaryHandler(
            source,
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(Tenant, ReportingPermissions.OrdersRead),
            new FixedTenantClock(),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(OrdersFilter(
                OtherAdvisor, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.SummarizedCriteria.Count);
        Assert.All(source.SummarizedCriteria, criteria => Assert.Equal(Caller, criteria.AdvisorId));
    }

    [Fact]
    public async Task OrdersSummaryWithAllAdvisorsKeepsEveryAdvisor()
    {
        var source = new FakeOrdersReportSource();
        var handler = new GetOrdersReportSummaryHandler(
            source,
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(
                Tenant, ReportingPermissions.OrdersRead, ReportingPermissions.AllAdvisorsRead),
            new FixedTenantClock(),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new GetOrdersReportSummaryQuery(OrdersFilter(advisorId: null)),
            TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(source.SummarizedCriteria).AdvisorId);
    }

    // ---- Listado de cotizaciones ----

    [Fact]
    public async Task QuotationsListingWithoutAllAdvisorsIgnoresTheRequestedAdvisorAndUsesTheCallers()
    {
        var source = new FakeQuotationsReportSource();
        var handler = QuotationsList(
            source,
            new FakeExecutionContext(Tenant, ReportingPermissions.QuotationRead),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new ListQuotationsReportQuery(QuotationsFilter(OtherAdvisor), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(Caller, source.LastCriteria?.AdvisorId);
    }

    [Fact]
    public async Task QuotationsListingWithAllAdvisorsPassesTheRequestedAdvisorThrough()
    {
        var source = new FakeQuotationsReportSource();
        var handler = QuotationsList(
            source,
            new FakeExecutionContext(
                Tenant, ReportingPermissions.QuotationRead, ReportingPermissions.AllAdvisorsRead),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new ListQuotationsReportQuery(QuotationsFilter(OtherAdvisor), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(OtherAdvisor, source.LastCriteria?.AdvisorId);
    }

    // ---- Resumen de cotizaciones ----

    [Fact]
    public async Task QuotationsSummaryWithoutAllAdvisorsScopesBothTheRangeAndThePrecedingWindow()
    {
        var source = new FakeQuotationsReportSource();
        var handler = new GetQuotationsReportSummaryHandler(
            source,
            new QuotationsReportFilterValidator(),
            new FakeExecutionContext(Tenant, ReportingPermissions.QuotationRead),
            new FixedTenantClock(),
            new FakeMembershipDirectory());

        await handler.HandleAsync(
            new GetQuotationsReportSummaryQuery(QuotationsFilter(
                advisorId: null, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31))),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, source.SummarizedCriteria.Count);
        Assert.All(source.SummarizedCriteria, criteria => Assert.Equal(Caller, criteria.AdvisorId));
    }

    [Fact]
    public async Task QuotationsSummaryWithoutAllAdvisorsAndWithoutAnActiveMembershipIsForbidden()
    {
        var source = new FakeQuotationsReportSource();
        var handler = new GetQuotationsReportSummaryHandler(
            source,
            new QuotationsReportFilterValidator(),
            new FakeExecutionContext(Tenant, ReportingPermissions.QuotationRead),
            new FixedTenantClock(),
            new FakeMembershipDirectory(activeMembershipId: null));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new GetQuotationsReportSummaryQuery(QuotationsFilter(advisorId: null)),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Empty(source.SummarizedCriteria);
    }

    private static ListOrdersReportHandler OrdersList(
        IOrdersReportSource source,
        FakeExecutionContext context,
        FakeMembershipDirectory directory) =>
        new(source, new OrdersReportFilterValidator(), context, new FixedTenantClock(), directory);

    private static ListQuotationsReportHandler QuotationsList(
        IQuotationsReportSource source,
        FakeExecutionContext context,
        FakeMembershipDirectory directory) =>
        new(source, new QuotationsReportFilterValidator(), context, new FixedTenantClock(), directory);

    private static OrdersReportFilter OrdersFilter(
        Guid? advisorId,
        DateOnly? from = null,
        DateOnly? to = null) =>
        new(Tenant, from, to, advisorId, ClientId: null, PaymentStatus: null);

    private static QuotationsReportFilter QuotationsFilter(
        Guid? advisorId,
        DateOnly? from = null,
        DateOnly? to = null) =>
        new(Tenant, from, to, advisorId, ClientId: null, Status: null);
}
