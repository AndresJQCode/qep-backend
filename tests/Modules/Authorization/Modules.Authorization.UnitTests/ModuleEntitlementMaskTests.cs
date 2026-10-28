using Modules.Authorization.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

/// <summary>Spec 2026-10-07, «Dónde se enchufa el enmascarado».</summary>
public sealed class ModuleEntitlementMaskTests
{
    private static readonly ModuleEntitlementMask Mask = new(
    [
        Definition("tenancy.settings.read", []),
        Definition("catalog.product.read", [TenantModuleKeys.Catalog]),
        Definition("reporting.orders.read", [TenantModuleKeys.Reporting, TenantModuleKeys.Orders]),
        Definition("legacy.unmapped", null),
    ]);

    private static readonly TenantModuleSet Everything = TenantModuleSet.FromStored(TenantModuleKeys.All);

    private static PermissionDefinition Definition(string permission, TenantModuleKey[]? modules) =>
        new(permission, permission, "", "Test", "low", modules);

    private static TenantModuleSet AllBut(params TenantModuleKey[] missing) =>
        TenantModuleSet.FromStored(TenantModuleKeys.All.Except(missing));

    [Fact]
    public void CorePassesEvenWithNoModules()
    {
        Assert.Equal(["tenancy.settings.read"], Mask.Apply(["tenancy.settings.read"], TenantModuleSet.Empty));
    }

    [Fact]
    public void APermissionFallsWithItsModule()
    {
        Assert.Empty(Mask.Apply(["catalog.product.read"], AllBut(TenantModuleKeys.Catalog)));
        Assert.Equal(["catalog.product.read"], Mask.Apply(["catalog.product.read"], Everything));
    }

    // Conjunción: un reporte exige reporting y su fuente.
    [Fact]
    public void APermissionWithTwoModulesNeedsBoth()
    {
        Assert.Empty(Mask.Apply(["reporting.orders.read"], AllBut(TenantModuleKeys.Orders)));
        Assert.Empty(Mask.Apply(["reporting.orders.read"], AllBut(TenantModuleKeys.Reporting)));
        Assert.Equal(["reporting.orders.read"], Mask.Apply(["reporting.orders.read"], Everything));
    }

    // Por dependencia: sin customers, orders no es efectivo aunque esté contratado.
    [Fact]
    public void AMissingDependencyMasksToo()
    {
        Assert.Empty(Mask.Apply(["reporting.orders.read"], AllBut(TenantModuleKeys.Customers)));
    }

    [Fact]
    public void AnUnmappedPermissionAlwaysFalls()
    {
        Assert.Empty(Mask.Apply(["legacy.unmapped"], Everything));
    }

    // Un X-Permissions inventado o un permiso de un módulo futuro (POS) en un rol custom.
    [Fact]
    public void AnUnregisteredPermissionFalls()
    {
        Assert.Empty(Mask.Apply(["pos.sale.read"], Everything));
    }

    // El índice sale de las definiciones registradas, no de IRoleCatalog.ListPermissions(): esa lista
    // sólo trae lo que concede algún rol de sistema.
    [Fact]
    public void ARegisteredPermissionThatNoSystemRoleGrantsIsResolvedByItsDefinition()
    {
        var definitions = new[] { Definition("catalog.product.read", [TenantModuleKeys.Catalog]) };
        var catalog = new RoleCatalog([new RoleDefinition("advisor", "Asesor", "", "Tenancy", "low", [])], definitions);

        Assert.DoesNotContain(catalog.ListPermissions(), permission => permission.Permission == "catalog.product.read");
        Assert.Equal(
            ["catalog.product.read"],
            new ModuleEntitlementMask(definitions).Apply(["catalog.product.read"], Everything));
    }

    [Fact]
    public void ApplyRemovesRepeatedPermissions()
    {
        Assert.Equal(
            ["tenancy.settings.read"],
            Mask.Apply(["tenancy.settings.read", "tenancy.settings.read"], Everything));
    }
}
