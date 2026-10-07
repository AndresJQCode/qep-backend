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
        Assert.Equal(48, dto.Columns.Count);
        Assert.All(dto.Columns, column => Assert.Equal("Catalog", column.Kind));
        // Sin fila, cada columna sale como nace: todas visibles menos "coordinadora_city"
        // (ajuste 2026-10-02), las "payment_method_N" (ajuste 2026-10-03) y "carrier" (ajuste
        // 2026-10-05), que nacen ocultas.
        Assert.All(dto.Columns, column => Assert.Equal(column.DefaultVisible, column.Visible));
        Assert.All(dto.Columns, column => Assert.Null(column.Value));
        Assert.Equal(new OrdersExportColumnDto("Catalog", "company", "EMPRESA", 1, true, "EMPRESA", null, true), dto.Columns[0]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "unit_price_without_tax", "Valor Unit sin IVA", 33, true, "Valor Unit sin IVA", null, true),
            dto.Columns[32]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "customer_name", "Cliente", 35, true, "Cliente", null, true),
            dto.Columns[34]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "customer_identification", "Documento de identidad", 36, true, "Documento de identidad", null, true),
            dto.Columns[35]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "bank_account", "Banco y cuenta", 37, true, "Banco y cuenta", null, true),
            dto.Columns[36]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "proof_amount_total", "Total consignado", 38, true, "Total consignado", null, true),
            dto.Columns[37]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "tax_rate", "Tasa IVA", 39, true, "Tasa IVA", null, true),
            dto.Columns[38]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "company_tax_id", "NIT Empresa", 40, true, "NIT Empresa", null, true),
            dto.Columns[39]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "payment_method_1", "Forma de pago 1", 42, false, "Forma de pago 1", null, false),
            dto.Columns[41]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "payment_method_5", "Forma de pago 5", 46, false, "Forma de pago 5", null, false),
            dto.Columns[45]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "carrier", "Transportadora", 47, false, "Transportadora", null, false),
            dto.Columns[46]);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "order_total", "Total facturado", 48, true, "Total facturado", null, true),
            dto.Columns[^1]);
    }

    // Ajuste 2026-10-02: DefaultVisible viaja por columna, como DefaultHeader y DefaultPosition,
    // para que "restaurar" sepa que la de Coordinadora vuelve oculta. Nulo en una fija.
    [Fact]
    public async Task EachCatalogColumnCarriesItsDefaultVisibilityAndAFixedOneNone()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        layout.Replace(
            [
                OrdersExportColumnSetting.Fixed("Tipo Doc", "FV", visible: true),
                OrdersExportColumnSetting.Catalog("coordinadora_city", "Ciudad (P4)", visible: true),
            ],
            Now);
        repository.Add(layout);
        var handler = NewHandler(repository);

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Null(dto.Columns[0].DefaultVisible);
        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "coordinadora_city", "Ciudad Coordinadora", 41, false, "Ciudad (P4)", null, true),
            dto.Columns[1]);
        // El resto nace visible, menos las "Forma de pago N" (ajuste 2026-10-03) y "carrier" (ajuste
        // 2026-10-05), que nacen ocultas.
        Assert.All(
            dto.Columns.Skip(2),
            column => Assert.Equal(
                !column.Key!.StartsWith("payment_method_", StringComparison.Ordinal) && column.Key != "carrier",
                column.DefaultVisible));
        Assert.Equal(5, dto.Columns.Count(column => column.DefaultVisible == false && column.Key!.StartsWith("payment_method_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithoutAStoredLayoutTheCoordinadoraCityComesAfterTheNitAndHidden()
    {
        var handler = NewHandler(new InMemoryOrdersExportLayoutRepository());

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(
            new OrdersExportColumnDto("Catalog", "coordinadora_city", "Ciudad Coordinadora", 41, false, "Ciudad Coordinadora", null, false),
            dto.Columns[40]);
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
        // 48 del catálogo (desde el ajuste 2026-10-05) + 1 fija.
        Assert.Equal(49, dto.Columns.Count);
        Assert.Equal(new OrdersExportColumnDto("Fixed", null, null, null, null, "Tipo Doc", "FV", true), dto.Columns[0]);
        Assert.Equal(new OrdersExportColumnDto("Catalog", "email", "Email", 19, true, "Correo", null, true), dto.Columns[1]);
        Assert.Equal(new OrdersExportColumnDto("Catalog", "company", "EMPRESA", 1, true, "EMPRESA", null, false), dto.Columns[2]);
        Assert.Equal("product_code", dto.Columns[3].Key);
    }

    // Spec 2026-10-05, D6: el nombre efectivo y el default viajan juntos (regla BFF), para que
    // "Restaurar todo" no tenga que saber de memoria cuál es el default.
    [Fact]
    public async Task WithoutAStoredLayoutTheSheetNameIsTheDefault()
    {
        var handler = NewHandler(new InMemoryOrdersExportLayoutRepository());

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal("Pedidos", dto.SheetName);
        Assert.Equal("Pedidos", dto.DefaultSheetName);
    }

    [Fact]
    public async Task AStoredLayoutComesOutWithItsSheetNameAndTheDefault()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var layout = OrdersExportLayout.CreateDefault(TenantId, Now);
        Assert.True(layout.Replace(OrdersExportLayout.Effective(stored: null), "MIGRACION 1", Now));
        repository.Add(layout);
        var handler = NewHandler(repository);

        var dto = await handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal("MIGRACION 1", dto.SheetName);
        Assert.Equal("Pedidos", dto.DefaultSheetName);
        Assert.Equal(2, dto.Version);
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

    // Spec 2026-10-07: el permiso es de núcleo (tenancy.settings.read) pero lo que se configura es de
    // orders, así que el handler lo chequea explícito, antes de leer el repositorio.
    [Fact]
    public async Task WithoutOrdersItIsForbiddenBeforeReadingTheRepository()
    {
        var repository = new InMemoryOrdersExportLayoutRepository();
        var modules = FixedTenantModules.WithoutOrders;
        var handler = NewHandler(repository, tenantModules: modules);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetOrdersExportLayoutQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(1, modules.FindCalls);
        Assert.Equal(0, repository.FindCalls);
    }

    private static GetOrdersExportLayoutHandler NewHandler(
        InMemoryOrdersExportLayoutRepository repository,
        IExecutionContext? executionContext = null,
        ITenantModules? tenantModules = null) =>
        new(
            repository,
            tenantModules ?? FixedTenantModules.Simulated,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId));
}
