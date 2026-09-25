using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>GET del layout (spec 2026-09-24): el efectivo (D8) con la versión implícita 1 sin
/// fila (D9), con defaultHeader y defaultPosition por columna (regla BFF), y 403 con tenant ajeno o
/// sin SettingsRead.</summary>
public sealed class GetOrdersExportLayoutHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithoutAStoredLayoutItReturnsTheCatalogAtVersionOne()
    {
        var handler = NewHandler(new InMemoryOrdersExportLayoutRepository());

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(TenantId, dto.TenantId);
        Assert.Equal(1, dto.Version);
        Assert.Equal(35, dto.Columns.Count);
        Assert.All(dto.Columns, column => Assert.Equal("Catalog", column.Kind));
        Assert.All(dto.Columns, column => Assert.True(column.Visible));
        Assert.All(dto.Columns, column => Assert.Null(column.Value));
        Assert.Equal(new OrdersExportColumnDto("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, true), dto.Columns[0]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "unit_price_without_tax", "Valor Unit sin IVA", 33, "Valor Unit sin IVA", null, true),
            dto.Columns[32]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "customer_name", "Cliente", 35, "Cliente", null, true),
            dto.Columns[34]);
    }

    [Fact]
    public async Task AStoredLayoutComesOutEffectiveWithItsVersionAndDefaults()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        layout.Replace(
            [
                OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
                OrdersExportColumnSetting.Catalog("email", "Correo", visible: true),
                OrdersExportColumnSetting.Catalog("company", "EMPRESA", visible: false),
            ],
            Now);
        repository.Add(layout);
        var handler = NewHandler(repository);

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(36, dto.Columns.Count);
        Assert.Equal(new OrdersExportColumnDto("Fixed", null, null, null, "Tipo Doc", "FV", true), dto.Columns[0]);
        Assert.Equal(new OrdersExportColumnDto("Catalog", "email", "Email", 19, "Correo", null, true), dto.Columns[1]);
        Assert.Equal(new OrdersExportColumnDto("Catalog", "company", "EMPRESA", 1, "EMPRESA", null, false), dto.Columns[2]);
        Assert.Equal("product_code", dto.Columns[3].Key);
    }

    [Fact]
    public async Task ForAnotherTenantIsForbiddenAndReadsNothing()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var handler = NewHandler(repository, new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal(0, repository.FindCalls);
    }

    // D5: permisos de settings, sin permiso nuevo.
    [Fact]
    public async Task WithoutSettingsReadIsForbidden()
    {
        var handler = NewHandler(
            new InMemoryOrdersExportLayoutRepository(),
            new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsRead));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken));
    }

    private static GetOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository, IExecutionContext? executionContext = null) =>
        new(repository, executionContext ?? new StubExecutionContext(SubjectId, TenantId));
}
