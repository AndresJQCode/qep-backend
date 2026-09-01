using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Audit.Application;
using Modules.Audit.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class InvitationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Token = "invitation-token-abc";
    private const string CorrelationId = "corr-1";

    // ---- Consulta por token ----

    [Fact]
    public async Task FindByTokenReturnsTheTenantNameAndAPendingStatus()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = TenantId.New();
        var membership = InvitedMembership(userId, tenantId, invitedAt: Now.AddHours(-1));
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")));

        var invitation = await service.FindByTokenAsync(
            Token,
            TestContext.Current.CancellationToken);

        Assert.Equal(tenantId, invitation.TenantId);
        Assert.Equal("Acme SAS", invitation.TenantName);
        Assert.Equal(userId, invitation.UserId);
        Assert.Equal(MembershipViewState.Pending, invitation.Status);
    }

    /// <summary>
    /// El vencimiento es perezoso (ver MembershipViewState): la fila puede seguir Invited
    /// con un ExpiresAt pasado, y la consulta tiene que reportar lo que la persona puede
    /// hacer con el link, no la columna cruda.
    /// </summary>
    [Fact]
    public async Task FindByTokenDerivesExpiredForALapsedInvitation()
    {
        var tenantId = TenantId.New();
        var membership = InvitedMembership(
            Guid.CreateVersion7(),
            tenantId,
            invitedAt: Now.AddHours(-100));
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")));

        var invitation = await service.FindByTokenAsync(
            Token,
            TestContext.Current.CancellationToken);

        Assert.Equal(MembershipViewState.Expired, invitation.Status);
    }

    [Fact]
    public async Task FindByTokenReportsAnAcceptedMembershipAsActive()
    {
        var tenantId = TenantId.New();
        var membership = InvitedMembership(
            Guid.CreateVersion7(),
            tenantId,
            invitedAt: Now.AddHours(-1));
        membership.Accept(Now.AddMinutes(-5));
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")));

        var invitation = await service.FindByTokenAsync(
            Token,
            TestContext.Current.CancellationToken);

        Assert.Equal(MembershipViewState.Active, invitation.Status);
    }

    [Fact]
    public async Task FindByTokenThrowsNotFoundForAnUnknownToken()
    {
        var service = Service(new MembershipRepo(), new TenantRepo());

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            service.FindByTokenAsync("unknown-token", TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.invitation.not_found", error.Code);
    }

    // ---- Aceptación ----

    [Fact]
    public async Task AcceptActivatesTheMembershipWithAuditAndOutboxEvent()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = TenantId.New();
        var membership = InvitedMembership(userId, tenantId, invitedAt: Now.AddHours(-1));
        membership.PullDomainEvents();
        var uow = new Uow();
        var audit = new Audit();
        var outbox = new Outbox();
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")),
            uow,
            audit,
            outbox);

        await service.AcceptAsync(
            Token,
            userId,
            CorrelationId,
            TestContext.Current.CancellationToken);

        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Equal(1, uow.Saves);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("tenancy.membership.accepted", entry.Action);
        // La persona que acepta es la actora de su propia aceptación, igual que en el
        // auto-accept del login (MembershipActivationService).
        Assert.Equal(userId, entry.ActorId);
        Assert.Equal(membership.Id.ToString(), entry.ResourceId);
        Assert.Equal("success", entry.Outcome);
        Assert.IsType<MembershipAcceptedDomainEvent>(Assert.Single(outbox.Events));
    }

    /// <summary>
    /// El link identifica una invitación, no autentica a quien lo abre: si la sesión
    /// pertenece a otro usuario se rechaza con código propio y sin tocar nada. Aceptar en
    /// nombre de otro sería adjudicarle una membresía a la cuenta equivocada.
    /// </summary>
    [Fact]
    public async Task AcceptForAnotherUserIsForbiddenAndLeavesTheMembershipUntouched()
    {
        var tenantId = TenantId.New();
        var membership = InvitedMembership(
            Guid.CreateVersion7(),
            tenantId,
            invitedAt: Now.AddHours(-1));
        membership.PullDomainEvents();
        var uow = new Uow();
        var audit = new Audit();
        var outbox = new Outbox();
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")),
            uow,
            audit,
            outbox);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            service.AcceptAsync(
                Token,
                Guid.CreateVersion7(),
                CorrelationId,
                TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.invitation.user_mismatch", error.Code);
        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(0, uow.Saves);
        Assert.Empty(audit.Entries);
        Assert.Empty(outbox.Events);
    }

    [Fact]
    public async Task AcceptWhenAlreadyActiveForTheSameUserIsIdempotent()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = TenantId.New();
        var membership = InvitedMembership(userId, tenantId, invitedAt: Now.AddHours(-1));
        membership.Accept(Now.AddMinutes(-10));
        membership.PullDomainEvents();
        var uow = new Uow();
        var audit = new Audit();
        var outbox = new Outbox();
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")),
            uow,
            audit,
            outbox);

        await service.AcceptAsync(
            Token,
            userId,
            CorrelationId,
            TestContext.Current.CancellationToken);

        // Sin segunda auditoría ni segundo evento: no pasó nada nuevo que registrar.
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Equal(0, uow.Saves);
        Assert.Empty(audit.Entries);
        Assert.Empty(outbox.Events);
    }

    [Fact]
    public async Task AcceptAnUnknownTokenThrowsNotFound()
    {
        var service = Service(new MembershipRepo(), new TenantRepo());

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            service.AcceptAsync(
                "unknown-token",
                Guid.CreateVersion7(),
                CorrelationId,
                TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.invitation.not_found", error.Code);
    }

    /// <summary>
    /// Igual que en el auto-accept del login, Accept marca Expired la invitación vencida.
    /// Acá la excepción fluye al mapeo central (422), pero la transición se persiste antes
    /// de re-lanzar: si no, el GET del mismo link seguiría derivando "expired" de un
    /// Invited fantasma en vez del estado real.
    /// </summary>
    [Fact]
    public async Task AcceptALapsedInvitationThrowsAndPersistsTheLazyExpiration()
    {
        var userId = Guid.CreateVersion7();
        var tenantId = TenantId.New();
        var membership = InvitedMembership(userId, tenantId, invitedAt: Now.AddHours(-100));
        membership.PullDomainEvents();
        var uow = new Uow();
        var service = Service(
            new MembershipRepo(membership),
            new TenantRepo(TenantNamed(tenantId, "Acme SAS")),
            uow);

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            service.AcceptAsync(
                Token,
                userId,
                CorrelationId,
                TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.membership.invitation_expired", error.Code);
        Assert.Equal(MembershipState.Expired, membership.State);
        Assert.Equal(1, uow.Saves);
    }

    // ---- Fixtures ----

    private static Membership InvitedMembership(
        Guid userId,
        TenantId tenantId,
        DateTimeOffset invitedAt) =>
        Membership.Invite(
            MembershipId.New(),
            userId,
            tenantId,
            ["advisor"],
            "invitation",
            Token,
            InvitationTokens.HashOf(Token),
            invitedAt,
            Membership.DefaultInvitationTimeToLive);

    private static Tenant TenantNamed(TenantId id, string displayName) =>
        Tenant.Create(id, "acme", displayName, "es-CO", "America/Bogota", "yyyy-MM-dd", Now);

    private static InvitationService Service(
        MembershipRepo memberships,
        TenantRepo tenants,
        Uow? uow = null,
        Audit? audit = null,
        Outbox? outbox = null) =>
        new(
            memberships,
            tenants,
            uow ?? new Uow(),
            audit ?? new Audit(),
            outbox ?? new Outbox(),
            new StubClock());

    private sealed class MembershipRepo(params Membership[] memberships) : IMembershipRepository
    {
        private readonly List<Membership> _memberships = [.. memberships];

        public Task<Membership?> FindByInvitationTokenHashAsync(
            string tokenHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(_memberships.SingleOrDefault(
                membership => membership.InvitationTokenHash == tokenHash));

        public Task<Membership?> FindByUserAndTenantAsync(
            Guid userId, TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Membership?>(null);

        public Task<Membership?> FindByIdAsync(
            MembershipId id, TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Membership?>(null);

        public Task<IReadOnlyList<Membership>> ListInvitedByUserAsync(
            Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([]);

        public Task<IReadOnlyList<TenantId>> ListActiveTenantsByUserAsync(
            Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantId>>([]);

        public Task<IReadOnlyList<Membership>> ListByUserAsync(
            Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([]);

        public Task<IReadOnlyList<Membership>> ListByTenantAsync(
            TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([]);

        public Task<IReadOnlyList<Membership>> ListActiveExcludingAsync(
            TenantId tenantId, MembershipId excludeId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Membership>>([]);

        public void Add(Membership membership) => _memberships.Add(membership);
    }

    private sealed class TenantRepo(params Tenant[] tenants) : ITenantRepository
    {
        private readonly List<Tenant> _tenants = [.. tenants];

        public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken) =>
            Task.FromResult(_tenants.SingleOrDefault(tenant => tenant.Id == id));

        public void Add(Tenant tenant) => _tenants.Add(tenant);
    }

    private sealed class Uow : ITenancyUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(
            string email,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by invitation flows.");
    }

    private sealed class Audit : IAuditRecorder
    {
        public List<(Guid? TenantId, Guid ActorId, string Action, string ResourceId, string Outcome)>
            Entries { get; } = [];

        public void Record(
            Guid? tenantId,
            Guid actorId,
            string action,
            string resourceType,
            string resourceId,
            string outcome,
            IReadOnlyCollection<string> changedFields,
            DateTimeOffset occurredAt,
            AuditActorType actorType = AuditActorType.Human,
            string source = "") =>
            Entries.Add((tenantId, actorId, action, resourceId, outcome));
    }

    private sealed class Outbox : IOutboxWriter
    {
        public List<IDomainEvent> Events { get; } = [];

        public void Add(IDomainEvent domainEvent, string correlationId) =>
            Events.Add(domainEvent);
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
