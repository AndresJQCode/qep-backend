using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Las transportadoras, los fletes y las opciones de envío las comparten cotizaciones y pedidos, que son
/// módulos independientes: el tenant que oculta cotizaciones y vende por pedidos tiene que seguir
/// configurándolos y leyéndolos, y el que no tiene ninguno de los dos, no.
/// </summary>
public sealed class SharedShippingModuleGuardTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static ListCarriersHandler ListCarriers(ITenantModules modules) =>
        new(new StubCarrierRepository(), new FixedQuotationSettingsStore(), new StubQuotationTaxRateLookup(null),
            modules, new StubExecutionContext(Guid.CreateVersion7(), TenantId));

    private static GetShippingOptionsHandler ShippingOptions(ITenantModules modules, IExecutionContext context) =>
        new(new StubCarrierRepository(), new FixedQuotationSettingsStore(), new StubQuotationTaxRateLookup(null),
            modules, context);

    [Fact]
    public async Task TheCarrierScreenOpensWithOnlyOrdersOn()
    {
        var screen = await ListCarriers(FixedTenantModules.WithoutQuotations)
            .HandleAsync(new ListCarriersQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(screen);
    }

    [Fact]
    public async Task TheCarrierScreenOpensWithOnlyQuotationsOn()
    {
        var screen = await ListCarriers(FixedTenantModules.WithoutOrders)
            .HandleAsync(new ListCarriersQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(screen);
    }

    [Fact]
    public async Task TheCarrierScreenIsForbiddenWithNeitherModuleOn()
    {
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            ListCarriers(FixedTenantModules.WithoutQuotationsNorOrders)
                .HandleAsync(new ListCarriersQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
    }

    // El asesor de pedidos no tiene el permiso de cotizar: con OrderRead alcanza para dibujar la entrega.
    [Fact]
    public async Task ShippingOptionsAreReadableWithOnlyOrderRead()
    {
        var context = new StubExecutionContext(Guid.CreateVersion7(), TenantId, QuotationsPermissions.QuotationRead);

        var options = await ShippingOptions(FixedTenantModules.WithoutQuotations, context)
            .HandleAsync(new GetShippingOptionsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(options);
    }

    [Fact]
    public async Task ShippingOptionsAreReadableWithOnlyQuotationRead()
    {
        var context = new StubExecutionContext(Guid.CreateVersion7(), TenantId, OrdersPermissions.OrderRead);

        var options = await ShippingOptions(FixedTenantModules.WithoutOrders, context)
            .HandleAsync(new GetShippingOptionsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.NotNull(options);
    }

    [Fact]
    public async Task ShippingOptionsAreDeniedWithoutEitherPermission()
    {
        var context = new StubExecutionContext(
            Guid.CreateVersion7(), TenantId, QuotationsPermissions.QuotationRead, OrdersPermissions.OrderRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            ShippingOptions(FixedTenantModules.Simulated, context)
                .HandleAsync(new GetShippingOptionsQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task ShippingOptionsAreForbiddenWithNeitherModuleOn()
    {
        var context = new StubExecutionContext(Guid.CreateVersion7(), TenantId);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            ShippingOptions(FixedTenantModules.WithoutQuotationsNorOrders, context)
                .HandleAsync(new GetShippingOptionsQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
    }
}
