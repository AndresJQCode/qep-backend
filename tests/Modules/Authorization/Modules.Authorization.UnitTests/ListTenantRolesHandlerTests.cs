using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

/// <summary>Spec 2026-10-08 §2: <c>GET /authorization/roles</c> oculta operator.* fuera del operador.</summary>
public sealed class ListTenantRolesHandlerTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private static readonly RoleCatalog Catalog = new(
        [new RoleDefinition("admin", "Administrador", "", "Tenancy", "high", ["advisorship.read", "operator.tenants.read"])],
        []);

    [Fact]
    public async Task OutsideTheOperatorTheAdminShowsNoOperatorPermission()
    {
        var roles = await Handler(operatorTenantId: Guid.CreateVersion7()).HandleAsync(
            new ListTenantRolesQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal(["advisorship.read"], roles.Single().Permissions);
    }

    [Fact]
    public async Task InsideTheOperatorTheAdminShowsThem()
    {
        var roles = await Handler(operatorTenantId: Tenant.Value).HandleAsync(
            new ListTenantRolesQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Contains("operator.tenants.read", roles.Single().Permissions);
    }

    private static ListTenantRolesHandler Handler(Guid operatorTenantId) =>
        new(new TenantRoleCatalog(Catalog, new NoCustomRoles()), new ReadContext(Tenant),
            new FixedOperatorTenant(operatorTenantId));

    private sealed class NoCustomRoles : ICustomRoleReader
    {
        public Task<IReadOnlyCollection<Role>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<Role>>([]);
    }

    private sealed class ReadContext(TenantId tenantId) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();
        public TenantId TenantId => tenantId;
        public bool HasPermission(string permission) => permission == TenancyPermissions.AdvisorshipRead;
    }
}
