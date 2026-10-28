using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class TenantModuleGuardTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Fact]
    public async Task AModuleThatIsOffIsForbiddenWithItsOwnCode()
    {
        var modules = new FixedTenantModules(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Orders])));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            TenantModuleGuard.EnsureEnabledAsync(
                modules, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal([TenantId], modules.Asked);
    }

    // Fail closed por dependencia: orders contratado sin quotations efectivo también es "apagado".
    [Fact]
    public async Task AModuleWithoutItsDependencyIsForbiddenToo()
    {
        var modules = new FixedTenantModules(
            TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Customers])));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            TenantModuleGuard.EnsureEnabledAsync(
                modules, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken));
        Assert.Equal([TenantId], modules.Asked);
    }

    // Sin el Assert sobre Asked, la prueba pasaría aunque el guard nunca consultara el puerto.
    [Fact]
    public async Task AnEnabledModulePasses()
    {
        var modules = FixedTenantModules.AllEnabled();

        await TenantModuleGuard.EnsureEnabledAsync(
            modules, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken);

        Assert.Equal([TenantId], modules.Asked);
    }

    // null = tenant simulado por el stub: no se bloquea (spec, «Puertos»), pero sí se consulta.
    [Fact]
    public async Task ATenantWithoutRowIsNotBlocked()
    {
        var modules = new FixedTenantModules(null);

        await TenantModuleGuard.EnsureEnabledAsync(
            modules, TenantId, TenantModuleKeys.Orders, TestContext.Current.CancellationToken);

        Assert.Equal([TenantId], modules.Asked);
    }
}
