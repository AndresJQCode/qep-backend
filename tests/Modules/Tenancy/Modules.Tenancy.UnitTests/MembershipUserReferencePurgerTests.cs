using Modules.Audit.Application;
using Modules.Audit.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// La purga de las membresías de un usuario huérfano (spec 2026-10-02). Identity la llama sólo
/// cuando ninguna sonda retiene al usuario, así que lo normal es encontrar quitadas o vencidas;
/// una viva es un error de quien llama y no se borra nada.
/// </summary>
public sealed class MembershipUserReferencePurgerTests
{
    private static readonly DateTimeOffset InvitedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _steps = [];
    private readonly RecordingTenancyUnitOfWork _unitOfWork;
    private readonly DetailedAuditRecorder _audit = new();

    public MembershipUserReferencePurgerTests()
    {
        _unitOfWork = new RecordingTenancyUnitOfWork(_steps);
    }

    [Fact]
    public void TheSourceIsTenancy()
    {
        var purger = Purger(new InMemoryMembershipRepository());

        Assert.Equal("tenancy", purger.Source);
    }

    [Fact]
    public async Task RemovedAndExpiredMembershipsAreDeletedAuditedAndSavedOnce()
    {
        var userId = Guid.CreateVersion7();
        var removed = Removed(userId);
        var expired = Expired(userId);
        var someoneElse = Removed(Guid.CreateVersion7());
        var memberships = new InMemoryMembershipRepository(removed, expired, someoneElse);

        await Purger(memberships).PurgeAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal([someoneElse], memberships.All);
        Assert.Equal(1, _unitOfWork.Commits);
        Assert.Equal(2, _audit.Entries.Count);
        foreach (var membership in new[] { removed, expired })
        {
            var entry = Assert.Single(_audit.Entries, entry => entry.ResourceId == membership.Id.ToString());
            Assert.Equal(membership.TenantId.Value, entry.TenantId);
            Assert.Equal(userId, entry.ActorId);
            Assert.Equal(AuditActorType.System, entry.ActorType);
            Assert.Equal("tenancy.membership.purged", entry.Action);
            Assert.Equal("membership", entry.ResourceType);
            Assert.Equal("success", entry.Outcome);
            Assert.Equal(Now, entry.OccurredAt);
        }
    }

    // Idempotente: el worker reintenta después de una falla parcial, y en el segundo intento la
    // purga ya pasó. Sin nada que borrar no se commitea ni se audita.
    [Fact]
    public async Task AUserWithoutMembershipsIsANoOp()
    {
        var memberships = new InMemoryMembershipRepository(Removed(Guid.CreateVersion7()));

        await Purger(memberships).PurgeAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.Single(memberships.All);
        Assert.Equal(0, _unitOfWork.Commits);
        Assert.Empty(_steps);
        Assert.Empty(_audit.Entries);
    }

    // Defensivo: la sonda ya garantizó que no hay vivas. Si aparece una, se corta antes de borrar
    // nada —tampoco la quitada que venía antes— y antes de commitear.
    [Fact]
    public async Task ALiveMembershipStopsThePurgeWithoutDeletingAnything()
    {
        var userId = Guid.CreateVersion7();
        var removed = Removed(userId);
        var live = Invited(userId);
        var memberships = new InMemoryMembershipRepository(removed, live);

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            Purger(memberships).PurgeAsync(userId, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.membership.not_purgeable", error.Code);
        Assert.Equal([removed, live], memberships.All);
        Assert.Equal(0, _unitOfWork.Commits);
        Assert.Empty(_audit.Entries);
    }

    private MembershipUserReferencePurger Purger(InMemoryMembershipRepository memberships) =>
        new(memberships, _unitOfWork, _audit, new FixedClock(Now));

    private static Membership Invited(Guid userId) =>
        Membership.Invite(
            MembershipId.New(),
            userId,
            TenantId.New(),
            "Ana Pérez",
            ["advisor"],
            "invitation",
            "plain-token",
            "plain-token-hash",
            InvitedAt,
            Membership.DefaultInvitationTimeToLive,
            advisorCode: 12);

    private static Membership Removed(Guid userId)
    {
        var membership = Invited(userId);
        membership.Remove(
            Tenant.Create(
                membership.TenantId, "qcode-demo", "QCode Demo", "es-CO", "America/Bogota",
                "yyyy-MM-dd", MembershipId.New(), InvitedAt),
            InvitedAt.AddHours(1));
        return membership;
    }

    private static Membership Expired(Guid userId)
    {
        var membership = Invited(userId);
        Assert.True(membership.Expire(
            InvitedAt + Membership.DefaultInvitationTimeToLive + TimeSpan.FromSeconds(1)));
        return membership;
    }

    private sealed record AuditRecord(
        Guid? TenantId,
        Guid ActorId,
        AuditActorType ActorType,
        string Action,
        string ResourceType,
        string ResourceId,
        string Outcome,
        DateTimeOffset OccurredAt);

    private sealed class DetailedAuditRecorder : IAuditRecorder
    {
        public List<AuditRecord> Entries { get; } = [];

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
            Entries.Add(new AuditRecord(
                tenantId, actorId, actorType, action, resourceType, resourceId, outcome, occurredAt));
    }
}
