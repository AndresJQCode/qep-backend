using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El handler de pedidos, tomado como representante de los cuatro listados: comparten forma
/// exacta, y lo que cambia entre ellos —el permiso y el origen— lo cubren las pruebas de
/// integracion endpoint por endpoint.
///
/// Lo que se verifica aca es lo que ninguna prueba de integracion puede aislar: que autorizar
/// pase **antes** que cualquier otra cosa y que al origen le llegue la paginacion ya normalizada.
/// </summary>
public sealed class OrdersReportHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");

    [Fact]
    public async Task ListingRejectsATenantThatIsNotTheCallersOne()
    {
        var source = new FakeOrdersReportSource();
        var handler = ListHandler(source, OtherTenant, ReportingPermissions.OrdersRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ListOrdersReportQuery(Filter(), 1, 50), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        // Y no llego a consultar: autorizar va primero, no despues de traer las filas.
        Assert.Null(source.LastCriteria);
    }

    [Fact]
    public async Task ListingRejectsACallerWithoutThePermission()
    {
        var source = new FakeOrdersReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.CustomerRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ListOrdersReportQuery(Filter(), 1, 50), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Null(source.LastCriteria);
    }

    [Fact]
    public async Task ListingNormalizesThePagingBeforeQuerying()
    {
        var source = new FakeOrdersReportSource { Total = 0 };
        var handler = ListHandler(source, Tenant, ReportingPermissions.OrdersRead);

        var page = await handler.HandleAsync(
            new ListOrdersReportQuery(Filter(), 0, 5_000), TestContext.Current.CancellationToken);

        Assert.Equal(1, source.LastPage);
        Assert.Equal(ReportPaging.MaxPageSize, source.LastPageSize);
        // El sobre devuelve la paginacion real, no la pedida: es como el llamador se entera de
        // que se le recorto.
        Assert.Equal(1, page.Page);
        Assert.Equal(ReportPaging.MaxPageSize, page.PageSize);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ListingParsesThePaymentStatusFilter()
    {
        var source = new FakeOrdersReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.OrdersRead);

        await handler.HandleAsync(
            new ListOrdersReportQuery(Filter("PartialPaymentReceived"), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            OrderPaymentStatusFilter.PartialPaymentReceived, source.LastCriteria?.PaymentStatus);
    }

    /// <summary>
    /// Un estado que no existe es <c>validation.failed</c> con el mapa <c>errors</c>, no un
    /// codigo de dominio: el frontend necesita saber **que control** marcar.
    /// </summary>
    [Fact]
    public async Task ListingRejectsAnUnknownPaymentStatus()
    {
        var source = new FakeOrdersReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.OrdersRead);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                new ListOrdersReportQuery(Filter("Refunded"), 1, 50),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "PaymentStatus");
        Assert.Null(source.LastCriteria);
    }

    private static ListOrdersReportHandler ListHandler(
        IOrdersReportSource source,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            new OrdersReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedTenantClock(),
            new FakeMembershipDirectory());

    // Spec 2026-09-17, punto 4: el reporte de diciembre va del 00:00 del 1 de diciembre al 00:00 del
    // 1 de enero en el huso del tenant, y el huso viaja para la serie mensual.
    [Fact]
    public async Task ListingCutsTheRangeAtTheTenantsLocalMidnights()
    {
        var source = new FakeOrdersReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.OrdersRead);

        await handler.HandleAsync(
            new ListOrdersReportQuery(
                new OrdersReportFilter(
                    Tenant, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), null, null, null),
                1,
                50),
            TestContext.Current.CancellationToken);

        Assert.NotNull(source.LastCriteria);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero), source.LastCriteria.Period.Start);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 5, 0, 0, TimeSpan.Zero), source.LastCriteria.Period.EndExclusive);
        Assert.Equal("America/Bogota", source.LastCriteria.Period.TimeZone.Id);
    }

    private static OrdersReportFilter Filter(string? paymentStatus = null) =>
        new(Tenant, From: null, To: null, AdvisorId: null, ClientId: null, paymentStatus);
}
