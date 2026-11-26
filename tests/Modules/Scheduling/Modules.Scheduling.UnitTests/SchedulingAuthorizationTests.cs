using BuildingBlocks.Application;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Scheduling.UnitTests;

// Spec 2026-11-21 (Agenda), «Aislamiento»: tenant distinto o permiso faltante → 403 authorization.denied (nunca 404); después, el módulo
// apagado → 403 tenancy.module_not_enabled. Mismo contrato que HrAuthorization.
public sealed class SchedulingAuthorizationTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public async Task ARequestForAnotherTenantIsDeniedNotNotFound()
    {
        var context = new Context(Guid.CreateVersion7(), SchedulingPermissions.AppointmentRead);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => SchedulingAuthorization.EnsureAsync(
            context, AllModules(), Tenant, CancellationToken.None, SchedulingPermissions.AppointmentRead));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task AManageWithoutItsReadIsDeniedBecauseEveryPermissionIsRequired()
    {
        var context = new Context(Tenant, SchedulingPermissions.AppointmentManage);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => SchedulingAuthorization.EnsureAsync(
            context, AllModules(), Tenant, CancellationToken.None,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage));

        Assert.Equal("authorization.denied", error.Code);
    }

    [Fact]
    public async Task WithTheSchedulingModuleOffTheErrorIsModuleNotEnabled()
    {
        var context = new Context(Tenant, SchedulingPermissions.AppointmentRead);
        var modules = new Modules(TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Scheduling)));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => SchedulingAuthorization.EnsureAsync(
            context, modules, Tenant, CancellationToken.None, SchedulingPermissions.AppointmentRead));

        Assert.Equal(TenantModuleGuard.ModuleNotEnabledCode, error.Code);
    }

    [Fact]
    public async Task ARequestWithItsPermissionsAndTheModuleOnPasses()
    {
        var context = new Context(Tenant, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);

        await SchedulingAuthorization.EnsureAsync(
            context, AllModules(), Tenant, CancellationToken.None,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);
    }

    [Fact]
    public void OnlyReadAllSeesEveryAgenda()
    {
        Assert.True(SchedulingAuthorization.CanSeeEveryAgenda(new Context(Tenant, SchedulingPermissions.AgendaReadAll)));
        Assert.False(SchedulingAuthorization.CanSeeEveryAgenda(
            new Context(Tenant, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage)));
    }

    private static Modules AllModules() => new(TenantModuleSet.FromStored(TenantModuleKeys.All));

    private sealed class Context(Guid tenantId, params string[] permissions) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => permissions.Contains(permission);
    }

    private sealed class Modules(TenantModuleSet? set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(set);
    }
}
