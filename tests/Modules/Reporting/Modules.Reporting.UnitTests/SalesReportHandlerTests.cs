using BuildingBlocks.Application;
using FluentValidation;
using Modules.Reporting.Application;
using Modules.Reporting.Domain;

namespace Modules.Reporting.UnitTests;

/// <summary>
/// El handler de ventas, tomado como representante de los ocho: los cuatro listados y las cuatro
/// exportaciones comparten forma exacta, y lo que cambia entre ellos —el permiso y el origen— lo
/// cubren las pruebas de integracion endpoint por endpoint.
///
/// Lo que se verifica aca es lo que ninguna prueba de integracion puede aislar: que autorizar
/// pase **antes** que cualquier otra cosa, que al origen le llegue la paginacion ya normalizada,
/// y que los dos limites de la exportacion se apliquen sobre lo que el origen devolvio.
/// </summary>
public sealed class SalesReportHandlerTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("01900000-0000-7000-8000-000000000002");

    [Fact]
    public async Task ListingRejectsATenantThatIsNotTheCallersOne()
    {
        var source = new FakeSalesReportSource();
        var handler = ListHandler(source, OtherTenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ListSalesReportQuery(Filter(), 1, 50), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        // Y no llego a consultar: autorizar va primero, no despues de traer las filas.
        Assert.Null(source.LastCriteria);
    }

    [Fact]
    public async Task ListingRejectsACallerWithoutThePermission()
    {
        var source = new FakeSalesReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.CustomerRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ListSalesReportQuery(Filter(), 1, 50), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Null(source.LastCriteria);
    }

    [Fact]
    public async Task ListingNormalizesThePagingBeforeQuerying()
    {
        var source = new FakeSalesReportSource { Total = 0 };
        var handler = ListHandler(source, Tenant, ReportingPermissions.SalesRead);

        var page = await handler.HandleAsync(
            new ListSalesReportQuery(Filter(), 0, 5_000), TestContext.Current.CancellationToken);

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
        var source = new FakeSalesReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.SalesRead);

        await handler.HandleAsync(
            new ListSalesReportQuery(Filter("PartialPaymentReceived"), 1, 50),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            SalePaymentStatusFilter.PartialPaymentReceived, source.LastCriteria?.PaymentStatus);
    }

    /// <summary>
    /// Un estado que no existe es <c>validation.failed</c> con el mapa <c>errors</c>, no un
    /// codigo de dominio: el frontend necesita saber **que control** marcar.
    /// </summary>
    [Fact]
    public async Task ListingRejectsAnUnknownPaymentStatus()
    {
        var source = new FakeSalesReportSource();
        var handler = ListHandler(source, Tenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(
                new ListSalesReportQuery(Filter("Refunded"), 1, 50),
                TestContext.Current.CancellationToken));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "PaymentStatus");
        Assert.Null(source.LastCriteria);
    }

    [Fact]
    public async Task ExportingAsksForOneRowMoreThanTheCap()
    {
        var source = new FakeSalesReportSource { Items = [Item()] };
        var builder = new FakeReportExcelBuilder();
        var handler = ExportHandler(source, builder, Tenant, ReportingPermissions.SalesRead);

        var file = await handler.HandleAsync(
            new ExportSalesReportQuery(Filter()), TestContext.Current.CancellationToken);

        Assert.Equal(ReportExportRules.ExportProbeLimit, source.LastExportLimit);
        Assert.Equal(1, builder.SalesRowCount);
        Assert.NotEmpty(file.Content);
    }

    [Fact]
    public async Task ExportingWithNoRowsFails()
    {
        var source = new FakeSalesReportSource { Items = [] };
        var builder = new FakeReportExcelBuilder();
        var handler = ExportHandler(source, builder, Tenant, ReportingPermissions.SalesRead);

        var error = await Assert.ThrowsAsync<ReportingDomainException>(() =>
            handler.HandleAsync(
                new ExportSalesReportQuery(Filter()), TestContext.Current.CancellationToken));

        Assert.Equal("reporting.export.empty", error.Code);
        // Y no se armo ningun archivo: un .xlsx con solo la cabecera es peor que el error.
        Assert.Null(builder.SalesRowCount);
    }

    [Fact]
    public async Task ExportingRejectsACallerWithoutThePermission()
    {
        var source = new FakeSalesReportSource { Items = [Item()] };
        var builder = new FakeReportExcelBuilder();
        var handler = ExportHandler(source, builder, Tenant, ReportingPermissions.QuotationRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new ExportSalesReportQuery(Filter()), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Null(source.LastExportLimit);
    }

    private static ListSalesReportHandler ListHandler(
        ISalesReportSource source,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            new SalesReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions));

    private static ExportSalesReportHandler ExportHandler(
        ISalesReportSource source,
        IReportExcelBuilder builder,
        Guid callerTenant,
        params string[] permissions) =>
        new(
            source,
            builder,
            new SalesReportFilterValidator(),
            new FakeExecutionContext(callerTenant, permissions),
            new FixedClock(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero)));

    private static SalesReportFilter Filter(string? paymentStatus = null) =>
        new(Tenant, From: null, To: null, AdvisorId: null, ClientId: null, paymentStatus);

    private static SalesReportItemDto Item() =>
        new(
            Guid.Parse("01900000-0000-7000-8000-0000000000b1"),
            "VEN-2026-0001",
            Guid.Parse("01900000-0000-7000-8000-0000000000b2"),
            "QUO-2026-0001",
            new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
            Guid.Parse("01900000-0000-7000-8000-0000000000b3"),
            "asesora@qcode.co",
            Guid.Parse("01900000-0000-7000-8000-0000000000b4"),
            "Cliente SA",
            "CLI08000001",
            "Approved",
            "FullPaymentReceived",
            100m,
            19m,
            119m);
}
