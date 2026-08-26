using Modules.Authorization.Application;
using Modules.Tenancy.Application;

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
    ],
    []);

    [Fact]
    public async Task DeniesWhenNoActiveMembership()
    {
        var service = new AuthorizationService(new FakeDirectory(null), Catalog);

        var decision = await service.AuthorizeAsync(
            Subject, Tenant, "tenancy.settings.read", TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal("no_active_membership", decision.ReasonCode);
    }

    [Fact]
    public async Task OwnerIsAllowedPrivilegedActions()
    {
        var service = new AuthorizationService(
            new FakeDirectory(["admin"]), Catalog);

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
        var service = new AuthorizationService(
            new FakeDirectory(["advisor"]), Catalog);

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
        var service = new AuthorizationService(
            new FakeDirectory(["admin", "advisor"]), Catalog);

        var permissions = await service.ResolvePermissionsAsync(
            Subject, Tenant, TestContext.Current.CancellationToken);

        Assert.NotNull(permissions);
        Assert.Equal(3, permissions!.Count);
        Assert.Contains("tenancy.settings.read", permissions);
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
    }
}
