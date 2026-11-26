using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// La caché de permisos (spec 2026-11-13, D-5) se invalida subiendo la versión de acceso del tenant antes del guardado y otra
/// vez después: el primer bump descarta lo que había, el segundo descarta lo que un request en carrera construyó con la versión
/// del medio. Cada caso de uso de membresía que cambia lo que alguien puede hacer en el tenant tiene que hacerlo.
/// </summary>
public sealed class TenantAccessVersionBumpTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public Fixture(Action<Membership>? arrange = null)
        {
            Tenant = Tenant.Create(TenantId.New(), "acme", "Acme", "es-CO", "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now);
            Member = Membership.CreateActive(MembershipId.New(), Guid.CreateVersion7(), Tenant.Id, ["advisor"], "invitation", Now);
            arrange?.Invoke(Member);
            Member.PullDomainEvents();
            Memberships = new InMemoryMembershipRepository(Member);
            Context = new OperatorContext(Tenant.Id, TenancyPermissions.AdvisorshipManage);
        }

        public List<string> Steps { get; } = [];
        public Tenant Tenant { get; }
        public Membership Member { get; }
        public InMemoryMembershipRepository Memberships { get; }
        public OperatorContext Context { get; }

        public StepsTenantRepository Tenants => new(Steps, Tenant);
        public StepsUnitOfWork UnitOfWork => new(Steps);
        public RecordingTenantAccessVersion Version => new(Steps);
        public StubUserDirectory Users { get; } = new(new Dictionary<Guid, string>());

        /// <summary>Sólo lo que importa al orden: el guardado y las subidas.</summary>
        public IReadOnlyList<string> BumpsAndSaves =>
            Steps.Where(step => step == "save" || step.StartsWith("bump:", StringComparison.Ordinal)).ToList();

        public string[] Expected => [$"bump:{Tenant.Id.Value}", "save", $"bump:{Tenant.Id.Value}"];
    }

    [Fact]
    public async Task UpdatingMemberRolesBumpsTheTenantVersionBeforeAndAfterSaving()
    {
        var f = new Fixture();
        var handler = new UpdateMemberRolesHandler(
            f.Memberships, f.Tenants, f.Users, new NoManagerRoles(), new KnownRoles(), f.UnitOfWork, f.Context,
            new RecordingAuditRecorder(), new NullOutbox(), new FixedClock(Now), new UpdateMemberRolesValidator(), f.Version,
            new MembershipPhoneReader(new NoHrPhones()));

        await handler.HandleAsync(
            new UpdateMemberRolesCommand(f.Tenant.Id, f.Member.Id, ["seller"], f.Member.Version, "corr"), Ct);

        Assert.Equal(f.Expected, f.BumpsAndSaves);
    }

    [Fact]
    public async Task SuspendingAMemberBumpsTheTenantVersionBeforeAndAfterSaving()
    {
        var f = new Fixture();
        var handler = new SuspendMemberHandler(
            f.Memberships, f.Tenants, f.Users, new NoManagerRoles(), f.UnitOfWork, f.Context, new RecordingAuditRecorder(),
            new NullOutbox(), new FixedClock(Now), f.Version, new MembershipPhoneReader(new NoHrPhones()));

        await handler.HandleAsync(new SuspendMemberCommand(f.Tenant.Id, f.Member.Id, "corr"), Ct);

        Assert.Equal(f.Expected, f.BumpsAndSaves);
    }

    [Fact]
    public async Task RemovingAMemberBumpsTheTenantVersionBeforeAndAfterSaving()
    {
        var f = new Fixture();
        var handler = new RemoveMemberHandler(
            f.Memberships, f.Tenants, f.Users, new NoManagerRoles(), f.UnitOfWork, f.Context, new RecordingAuditRecorder(),
            new NullOutbox(), new FixedClock(Now), f.Version, new MembershipPhoneReader(new NoHrPhones()));

        await handler.HandleAsync(new RemoveMemberCommand(f.Tenant.Id, f.Member.Id, "corr"), Ct);

        Assert.Equal(f.Expected, f.BumpsAndSaves);
    }

    [Fact]
    public async Task ReactivatingAMemberBumpsTheTenantVersionBeforeAndAfterSaving()
    {
        var f = new Fixture();
        f.Member.Suspend(f.Tenant, Now);
        f.Member.PullDomainEvents();
        var handler = new ReactivateMemberHandler(
            f.Memberships, f.Tenants, f.Users, f.UnitOfWork, f.Context, new RecordingAuditRecorder(), new NullOutbox(),
            new FixedClock(Now), f.Version, new MembershipPhoneReader(new NoHrPhones()));

        await handler.HandleAsync(new ReactivateMemberCommand(f.Tenant.Id, f.Member.Id, "corr"), Ct);

        Assert.Equal(f.Expected, f.BumpsAndSaves);
    }

    [Fact]
    public async Task AcceptingInvitationsBumpsEveryAcceptedTenant()
    {
        var steps = new List<string>();
        var userId = Guid.CreateVersion7();
        var first = TenantId.New();
        var second = TenantId.New();
        var invitations = new InvitedMemberships(
            Invited(userId, first, Now.AddHours(-1)), Invited(userId, second, Now.AddHours(-1)));
        var service = new MembershipActivationService(
            invitations, new StepsUnitOfWork(steps), new RecordingAuditRecorder(), new NullOutbox(), new FixedClock(Now),
            new RecordingTenantAccessVersion(steps));

        await service.AcceptInvitedMembershipsAsync(userId, "corr", Ct);

        var saveAt = steps.IndexOf("save");
        Assert.Equal(
            new[] { $"bump:{first.Value}", $"bump:{second.Value}" }.Order(),
            steps.Take(saveAt).Order());
        Assert.Equal(
            new[] { $"bump:{first.Value}", $"bump:{second.Value}" }.Order(),
            steps.Skip(saveAt + 1).Order());
    }

    [Fact]
    public async Task AnExpiredInvitationDoesNotBump()
    {
        var steps = new List<string>();
        var userId = Guid.CreateVersion7();
        var invitations = new InvitedMemberships(Invited(userId, TenantId.New(), Now.AddDays(-30)));
        var service = new MembershipActivationService(
            invitations, new StepsUnitOfWork(steps), new RecordingAuditRecorder(), new NullOutbox(), new FixedClock(Now),
            new RecordingTenantAccessVersion(steps));

        await service.AcceptInvitedMembershipsAsync(userId, "corr", Ct);

        Assert.DoesNotContain(steps, step => step.StartsWith("bump:", StringComparison.Ordinal));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Membership Invited(Guid userId, TenantId tenantId, DateTimeOffset invitedAt)
    {
        var membership = Membership.Invite(
            MembershipId.New(),
            userId,
            tenantId,
            "Ana Pérez",
            ["advisor"],
            "invitation",
            $"token-{Guid.NewGuid():N}",
            InvitationTokens.HashOf($"token-{Guid.NewGuid():N}"),
            invitedAt,
            Membership.DefaultInvitationTimeToLive);
        membership.PullDomainEvents();
        return membership;
    }

    private sealed class NoManagerRoles : IRolePermissionChecker
    {
        public Task<bool> AnyGrantsAsync(
            TenantId tenantId, IReadOnlyCollection<string> roles, string permission, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class KnownRoles : IRoleReferenceValidator
    {
        public Task<RoleReference> CheckAsync(TenantId tenantId, string role, CancellationToken cancellationToken) =>
            Task.FromResult(RoleReference.Known);
    }

    private sealed class NullOutbox : IOutboxWriter
    {
        public void Add(IDomainEvent domainEvent, string correlationId)
        {
        }
    }

    /// <summary>Sólo lo que usa el auto-accept del login: las invitadas del usuario y sus tenants activos después.</summary>
    private sealed class InvitedMemberships(params Membership[] memberships) : IMembershipRepository
    {
        public Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>(memberships.Where(m => m.UserId == userId).ToList());

        public Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantId>>(
                memberships.Where(m => m.State == MembershipState.Active).Select(m => m.TenantId).ToList());

        public Task<Membership?> FindByInvitationTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Membership?> FindByUserAndTenantAsync(Guid userId, TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Membership?> FindByIdAsync(MembershipId id, TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ActiveTenantSummary>> ListActiveTenantSummariesByUserAsync(
            Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Membership>> ListByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Membership>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Membership>> ListByIdsAsync(
            TenantId tenantId, IReadOnlyCollection<MembershipId> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
            TenantId tenantId, MembershipId excludeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsAdvisorCodeTakenAsync(
            TenantId tenantId, int advisorCode, MembershipId? exceptMembershipId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Membership membership) => throw new NotSupportedException();

        public void Remove(Membership membership) => throw new NotSupportedException();
    }
}
