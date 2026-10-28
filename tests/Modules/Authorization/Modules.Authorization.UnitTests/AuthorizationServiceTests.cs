using Modules.Authorization.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class AuthorizationServiceTests
{
    private static readonly Guid Subject = Guid.CreateVersion7();
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static readonly RoleCatalog Catalog = new(
    [
        new RoleDefinition("admin",
            "Owner",
            "Owner role",
            "Tenancy",
            "high",
            ["tenancy.settings.read", "tenancy.settings.update", "advisorship.invite"]),
        new RoleDefinition("advisor",
            "Member",
            "Member role",
            "Tenancy",
            "medium",
            ["tenancy.settings.read"]),
        new RoleDefinition("seller",
            "Seller",
            "Reads the catalog",
            "Catalog",
            "low",
            ["tenancy.settings.read", "catalog.product.read"]),
        new RoleDefinition("operator-admin",
            "Operator",
            "Operator role",
            "Tenancy",
            "high",
            ["tenancy.settings.read", "operator.tenants.read"]),
    ],
    []);

    private static readonly ModuleEntitlementMask Mask = new(
    [
        new PermissionDefinition("tenancy.settings.read", "", "", "Tenancy", "low", []),
        new PermissionDefinition("tenancy.settings.update", "", "", "Tenancy", "high", []),
        new PermissionDefinition("advisorship.invite", "", "", "Tenancy", "medium", []),
        new PermissionDefinition("catalog.product.read", "", "", "Catalog", "low", [TenantModuleKeys.Catalog]),
        new PermissionDefinition("operator.tenants.read", "", "", "Operator", "medium", []),
    ]);

    private static AuthorizationService NewService(
        IReadOnlyCollection<string>? roles, TenantModuleSet? modules, Guid? operatorTenantId = null) =>
        new(new FakeDirectory(roles), TenantCatalog(), new FixedTenantModules(modules), Mask,
            new FixedOperatorTenant(operatorTenantId));

    // Los casos de antes no son sobre módulos: con los siete prendidos significan lo mismo que
    // antes. Con null no, porque en el camino real null es fail closed (sólo núcleo).
    private static AuthorizationService NewService(IReadOnlyCollection<string>? roles) =>
        NewService(roles, TenantModuleSet.FromStored(TenantModuleKeys.All));

    /// <summary>
    /// El servicio pasó a resolver contra el catálogo del tenant. Se envuelve el de sistema
    /// sin roles custom: lo que estos casos ejercen es la decisión de autorización, no la
    /// fusión — de eso se ocupa `TenantRoleCatalogTests`.
    /// </summary>
    private static TenantRoleCatalog TenantCatalog() =>
        new TenantRoleCatalog(Catalog, new NoCustomRoles());

    private sealed class NoCustomRoles : ICustomRoleReader
    {
        public Task<IReadOnlyCollection<Modules.Authorization.Domain.Role>> ListAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<Modules.Authorization.Domain.Role>>([]);
    }

    [Fact]
    public async Task DeniesWhenNoActiveMembership()
    {
        var service = NewService(null);

        var decision = await service.AuthorizeAsync(
            Subject, Tenant, "tenancy.settings.read", TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal("no_active_membership", decision.ReasonCode);
    }

    [Fact]
    public async Task OwnerIsAllowedPrivilegedActions()
    {
        var service = NewService(["admin"]);

        Assert.True((await service.AuthorizeAsync(
            Subject, Tenant, "tenancy.settings.update",
            TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await service.AuthorizeAsync(
            Subject, Tenant, "advisorship.invite",
            TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task MemberIsDeniedPrivilegedActionsButAllowedRead()
    {
        var service = NewService(["advisor"]);

        Assert.True((await service.AuthorizeAsync(
            Subject, Tenant, "tenancy.settings.read",
            TestContext.Current.CancellationToken)).Allowed);

        var denied = await service.AuthorizeAsync(
            Subject, Tenant, "tenancy.settings.update",
            TestContext.Current.CancellationToken);
        Assert.False(denied.Allowed);
        Assert.Equal("permission_denied", denied.ReasonCode);
    }

    [Fact]
    public async Task ResolvePermissionsDedupesAcrossRoles()
    {
        var service = NewService(["admin", "advisor"]);

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.NotNull(permissions);
        Assert.Equal(3, permissions!.Count);
        Assert.Contains("tenancy.settings.read", permissions);
    }

    [Fact]
    public async Task ResolvePermissionsMasksTheOnesOfAModuleThatIsOff()
    {
        var service = NewService(
            ["seller"], TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Catalog])));

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(["tenancy.settings.read"], permissions);
    }

    // Hay membresía activa, luego hay tenant: null no debería pasar. Si pasa, fail closed.
    [Fact]
    public async Task WithoutATenantRowOnlyCoreSurvives()
    {
        var service = NewService(["seller"], modules: null);

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(["tenancy.settings.read"], permissions);
    }

    [Fact]
    public async Task AuthorizeDeniesAMaskedPermission()
    {
        var service = NewService(["seller"], TenantModuleSet.Empty);

        var decision = await service.AuthorizeAsync(
            Subject, Tenant, "catalog.product.read", TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal("permission_denied", decision.ReasonCode);
    }

    // Spec 2026-10-08 §2: el filtro va después del enmascarado; el admin del operador los conserva.
    [Fact]
    public async Task TheOperatorTenantKeepsItsOperatorPermissions()
    {
        var service = NewService(["operator-admin"], TenantModuleSet.FromStored(TenantModuleKeys.All), Tenant);

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Contains("operator.tenants.read", permissions!);
    }

    [Fact]
    public async Task AnyOtherTenantLosesThemEvenWithTheRole()
    {
        var service = NewService(["operator-admin"], TenantModuleSet.FromStored(TenantModuleKeys.All), Guid.CreateVersion7());

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.Equal(["tenancy.settings.read"], permissions);
    }

    [Fact]
    public void CatalogReturnsEmptyForUnknownRole()
    {
        Assert.Empty(Catalog.PermissionsFor("tenancy.unknown"));
    }

    [Fact]
    public void CatalogReturnsRoleAndPermissionMetadata()
    {
        Assert.Contains(Catalog.ListRoles(), role => role.DisplayName == "Owner");
        Assert.Contains(Catalog.ListPermissions(), permission =>
            permission.Permission == "tenancy.settings.read");
        Assert.False(string.IsNullOrWhiteSpace(Catalog.CatalogVersion));
    }

    private sealed class FakeDirectory(IReadOnlyCollection<string>? roles)
        : IMembershipDirectory
    {
        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
            Guid userId,
            Guid tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(roles);

        public Task<Guid?> FindActiveMembershipIdAsync(
            Guid userId,
            Guid tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class FixedTenantModules(TenantModuleSet? set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(set);
    }
}
