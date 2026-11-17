using Modules.Authorization.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

/// <summary>
/// Spec 2026-11-15 (roles asignables): un rol se puede asignar si concede al menos un permiso que el
/// tenant ofrece. Es la misma regla con la que el catálogo filtra los permisos de cada rol.
/// </summary>
public sealed class RoleAssignabilityTests
{
    private static readonly ModuleEntitlementMask Mask = new(
    [
        Definition("pos.sale.read", [TenantModuleKeys.Pos]),
        Definition("tenancy.settings.read", []),
        Definition("operator.usage.read", []),
    ]);

    private static readonly TenantModuleSet Everything = TenantModuleSet.FromStored(TenantModuleKeys.All);

    private static PermissionDefinition Definition(string permission, TenantModuleKey[]? modules) =>
        new(permission, permission, "", "Test", "low", modules);

    [Fact]
    public void ARoleWithOnlyPosPermissionsIsNotAssignableWithoutPos()
    {
        Assert.False(RoleAssignability.IsAssignable(["pos.sale.read"], TenantModuleSet.Empty, false, Mask));
    }

    // Everything y no sólo pos: pos depende de catalog y companies para ser efectivo.
    [Fact]
    public void ARoleWithOnlyPosPermissionsIsAssignableWithPos()
    {
        Assert.True(RoleAssignability.IsAssignable(["pos.sale.read"], Everything, false, Mask));
    }

    [Fact]
    public void AMixedRoleIsAssignableWithoutPos()
    {
        string[] permissions = ["pos.sale.read", "tenancy.settings.read"];

        Assert.True(RoleAssignability.IsAssignable(permissions, TenantModuleSet.Empty, false, Mask));
        Assert.Equal(
            ["tenancy.settings.read"],
            RoleAssignability.OfferedPermissions(permissions, TenantModuleSet.Empty, false, Mask));
    }

    [Fact]
    public void AnOperatorOnlyRoleIsNotAssignableOutsideTheOperatorTenant()
    {
        Assert.False(RoleAssignability.IsAssignable(["operator.usage.read"], Everything, false, Mask));
        Assert.True(RoleAssignability.IsAssignable(["operator.usage.read"], Everything, true, Mask));
    }

    // Tenant ausente de tenancy.tenants (sólo con el stub de desarrollo): no hay nada que enmascarar.
    [Fact]
    public void EveryRoleIsAssignableWhenTheTenantIsUnknown()
    {
        Assert.True(RoleAssignability.IsAssignable(["pos.sale.read"], null, false, Mask));
    }
}
