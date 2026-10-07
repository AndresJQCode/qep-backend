using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>Spec 2026-10-07, «Archivos de Storage: el dueño decide el módulo».</summary>
public sealed class FileOwnerModulesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // Un miembro nuevo de FileOwnerType sin mapear no pasa: criterio de éxito 4.
    [Fact]
    public void EveryOwnerTypeIsMapped()
    {
        Assert.Equal(
            Enum.GetValues<FileOwnerType>().Order(),
            FileOwnerModules.ByOwnerType.Keys.Order());
    }

    // El mapa se lee en cada request y no cambia nunca: congelado, no un Dictionary mutable.
    [Fact]
    public void TheMapIsFrozen()
    {
        Assert.IsType<System.Collections.Frozen.FrozenDictionary<FileOwnerType, TenantModuleKey?>>(
            FileOwnerModules.ByOwnerType, exactMatch: false);
    }

    [Fact]
    public void ProductsAreCatalogAndProofsAreOrdersAndTheRestIsCore()
    {
        Assert.Same(TenantModuleKeys.Catalog, FileOwnerModules.ModuleOf(FileOwnerType.Product));
        Assert.Same(TenantModuleKeys.Orders, FileOwnerModules.ModuleOf(FileOwnerType.PaymentProof));
        foreach (var core in new[] { FileOwnerType.User, FileOwnerType.Entity, FileOwnerType.System, FileOwnerType.Tenant })
        {
            Assert.Null(FileOwnerModules.ModuleOf(core));
        }
    }

    [Fact]
    public void OwnerTypesDisabledInListsTheOnesWhoseModuleIsOff()
    {
        Assert.Equal(
            [FileOwnerType.PaymentProof],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(
                TenantModuleKeys.All.Except([TenantModuleKeys.Orders]))));
        Assert.Equal(
            [FileOwnerType.Product, FileOwnerType.PaymentProof],
            FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.Empty).Order());
        Assert.Empty(FileOwnerModules.OwnerTypesDisabledIn(TenantModuleSet.FromStored(TenantModuleKeys.All)));
    }

    [Theory]
    [InlineData(FileOwnerType.Product, "catalog")]
    [InlineData(FileOwnerType.PaymentProof, "orders")]
    public async Task AFileWhoseModuleIsOffIsForbidden(FileOwnerType ownerType, string module)
    {
        var modules = FixedTenantModules.AllBut(TenantModuleKey.Parse(module));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
                modules, FileOf(ownerType), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal([TenantId], modules.Asked);
    }

    [Theory]
    [InlineData(FileOwnerType.Tenant)]
    [InlineData(FileOwnerType.User)]
    public async Task ACoreFilePassesWithNoModules(FileOwnerType ownerType)
    {
        var modules = new FixedTenantModules(TenantModuleSet.Empty);

        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
            modules, FileOf(ownerType), TestContext.Current.CancellationToken);

        Assert.Empty(modules.Asked);
    }

    // Preflight F-03: sin la aserción, la prueba pasaría aunque el guard nunca consultara el puerto.
    [Fact]
    public async Task ASimulatedTenantIsNotBlocked()
    {
        var modules = FixedTenantModules.Simulated;

        await FileOwnerModuleGuard.EnsureOwnerModuleEnabledAsync(
            modules, FileOf(FileOwnerType.PaymentProof), TestContext.Current.CancellationToken);

        Assert.Equal([TenantId], modules.Asked);
    }

    private static FileResource FileOf(FileOwnerType ownerType) =>
        FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), ownerType,
            "archivo.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/archivo", Now);
}
