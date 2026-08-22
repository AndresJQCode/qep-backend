using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class MembershipTests
{
    private static readonly DateTimeOffset InvitedAt =
        new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Ttl = Membership.DefaultInvitationTimeToLive;

    [Fact]
    public void InviteStartsInvitedAndRaisesEventWithExpiry()
    {
        var userId = Guid.CreateVersion7();
        var membership = Invite(userId);

        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(InvitedAt + Ttl, membership.ExpiresAt);
        Assert.Null(membership.AcceptedAt);
        var domainEvent = Assert.Single(membership.DomainEvents);
        var invited = Assert.IsType<MembershipInvitedDomainEvent>(domainEvent);
        Assert.Equal(membership.Id, invited.MembershipId);
        Assert.Equal(userId, invited.UserId);
        Assert.Equal(membership.ExpiresAt, invited.ExpiresAt);
    }

    [Fact]
    public void AcceptWithinWindowTransitionsToActiveAndRaisesEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.Accept(InvitedAt.AddHours(1));

        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Equal(InvitedAt.AddHours(1), membership.AcceptedAt);
        Assert.IsType<MembershipAcceptedDomainEvent>(Assert.Single(membership.DomainEvents));
    }

    [Fact]
    public void AcceptWhenAlreadyActiveIsIdempotent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt.AddHours(1));
        membership.PullDomainEvents();

        membership.Accept(InvitedAt.AddHours(2));

        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Empty(membership.DomainEvents);
    }

    [Fact]
    public void AcceptAfterExpiryThrowsAndMarksExpired()
    {
        var membership = Invite(Guid.CreateVersion7());

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Accept(InvitedAt + Ttl + TimeSpan.FromSeconds(1)));

        Assert.Equal("tenancy.membership.invitation_expired", exception.Code);
        Assert.Equal(MembershipState.Expired, membership.State);
    }

    [Fact]
    public void ExpireOnlyAppliesToInvitedPastWindow()
    {
        var membership = Invite(Guid.CreateVersion7());

        Assert.False(membership.Expire(InvitedAt.AddHours(1)));
        Assert.Equal(MembershipState.Invited, membership.State);

        Assert.True(membership.Expire(InvitedAt + Ttl + TimeSpan.FromSeconds(1)));
        Assert.Equal(MembershipState.Expired, membership.State);
    }

    [Fact]
    public void SuspendActiveTransitionsToSuspendedAndRaisesEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt.AddHours(1));
        membership.PullDomainEvents();

        membership.Suspend(InvitedAt.AddHours(2));

        Assert.Equal(MembershipState.Suspended, membership.State);
        var domainEvent = Assert.Single(membership.DomainEvents);
        var suspended = Assert.IsType<MembershipSuspendedDomainEvent>(domainEvent);
        Assert.Equal(membership.Id, suspended.MembershipId);
    }

    [Fact]
    public void SuspendNonActiveThrows()
    {
        var membership = Invite(Guid.CreateVersion7());

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Suspend(InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.not_active", exception.Code);
        Assert.Equal(MembershipState.Invited, membership.State);
    }

    [Fact]
    public void RemoveFromInvitedTransitionsToRemovedAndRaisesEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.Remove(InvitedAt.AddHours(1));

        Assert.Equal(MembershipState.Removed, membership.State);
        var domainEvent = Assert.Single(membership.DomainEvents);
        var removed = Assert.IsType<MembershipRemovedDomainEvent>(domainEvent);
        Assert.Equal(membership.Id, removed.MembershipId);
    }

    [Fact]
    public void RemoveFromSuspendedTransitionsToRemoved()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt.AddHours(1));
        membership.Suspend(InvitedAt.AddHours(2));

        membership.Remove(InvitedAt.AddHours(3));

        Assert.Equal(MembershipState.Removed, membership.State);
    }

    [Fact]
    public void RemoveAlreadyRemovedThrows()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Remove(InvitedAt.AddHours(1));

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Remove(InvitedAt.AddHours(2)));

        Assert.Equal("tenancy.membership.already_terminal", exception.Code);
    }

    [Fact]
    public void RemoveExpiredThrows()
    {
        var membership = Invite(Guid.CreateVersion7());
        Assert.True(membership.Expire(InvitedAt + Ttl + TimeSpan.FromSeconds(1)));

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Remove(InvitedAt + Ttl + TimeSpan.FromHours(1)));

        Assert.Equal("tenancy.membership.already_terminal", exception.Code);
    }

    [Fact]
    public void ChangeRolesNormalizesRolesAndRaisesEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.ChangeRoles(
            [" admin ", "admin", "advisor"],
            InvitedAt.AddHours(1));

        Assert.Equal(["admin", "advisor"], membership.Roles);
        var domainEvent = Assert.Single(membership.DomainEvents);
        var changed = Assert.IsType<MembershipRolesChangedDomainEvent>(domainEvent);
        Assert.Equal(["advisor"], changed.PreviousRoles);
        Assert.Equal(membership.Roles, changed.NewRoles);
    }

    [Fact]
    public void ChangeRolesRequiresAtLeastOneRole()
    {
        var membership = Invite(Guid.CreateVersion7());

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.ChangeRoles(["  "], InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.roles_required", exception.Code);
    }

    [Fact]
    public void ChangeRolesForRemovedMembershipThrows()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Remove(InvitedAt.AddHours(1));

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.ChangeRoles(["admin"], InvitedAt.AddHours(2)));

        Assert.Equal("tenancy.membership.already_terminal", exception.Code);
    }

    [Fact]
    public void InviteWithEmptyUserThrows()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.Empty,
                TenantId.New(),
                [],
                "invitation",
                InvitedAt,
                Ttl));

        Assert.Equal("tenancy.membership.user_required", exception.Code);
    }


    /// <summary>
    /// AUTH-05 / SDD-OD-04. Una invitación que vence sin que la persona haya intentado
    /// entrar queda en <see cref="MembershipState.Invited"/> con un ExpiresAt pasado,
    /// porque el vencimiento es perezoso: sólo Accept la transiciona. Volver a invitar
    /// devolvía esa fila muerta sin cambios, dejando a la persona permanentemente
    /// no-invitable mientras el admin creía que la invitación se había mandado.
    ///
    /// La renovación pasa en el lugar, no insertando una segunda fila: (UserId, TenantId) es
    /// un índice UNIQUE (TenancyDbContext.cs:105), así que un usuario tiene exactamente una
    /// membresía por tenant. Ver SDD-CT-15.
    /// </summary>
    [Fact]
    public void ReinviteAfterExpiryRenewsInPlaceWithAFreshWindow()
    {
        var membership = Invite(Guid.CreateVersion7());
        var originalId = membership.Id;
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(["tenancy.admin"], lapsed, Ttl);

        Assert.Equal(originalId, membership.Id);
        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(lapsed, membership.InvitedAt);
        Assert.Equal(lapsed + Ttl, membership.ExpiresAt);
        Assert.True(membership.ExpiresAt > lapsed);
        Assert.Null(membership.AcceptedAt);
        Assert.Equal(["tenancy.admin"], membership.Roles);
    }

    [Fact]
    public void ReinviteRaisesTheInvitedEventSoTheEmailIsSentAgain()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(["advisor"], lapsed, Ttl);

        var domainEvent = Assert.Single(membership.DomainEvents);
        var invited = Assert.IsType<MembershipInvitedDomainEvent>(domainEvent);
        Assert.Equal(membership.Id, invited.MembershipId);
        Assert.Equal(membership.ExpiresAt, invited.ExpiresAt);
    }

    [Fact]
    public void ReinviteWorksFromTheExpiredStateToo()
    {
        var membership = Invite(Guid.CreateVersion7());
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);
        Assert.True(membership.Expire(lapsed));

        membership.Reinvite(["advisor"], lapsed, Ttl);

        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(lapsed + Ttl, membership.ExpiresAt);
    }

    /// <summary>
    /// CA-AUTH-05-12: una invitación viva no se toca. Renovarla extendería en silencio una
    /// ventana con la que alguien cuenta, e invalidaría el link ya enviado.
    /// </summary>
    [Fact]
    public void ReinviteRejectsAStillValidInvitation()
    {
        var membership = Invite(Guid.CreateVersion7());
        var withinWindow = InvitedAt + TimeSpan.FromHours(1);

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(["advisor"], withinWindow, Ttl));

        Assert.Equal("tenancy.membership.invitation_still_valid", error.Code);
        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(InvitedAt + Ttl, membership.ExpiresAt);
    }

    [Fact]
    public void ReinviteRejectsAnActiveMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                ["advisor"],
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Active, membership.State);
    }

    /// <summary>
    /// La frontera de seguridad que la revisión de AUTH-05 encontró sin probar. Suspender y quitar
    /// son actos deliberados de un administrador; re-invitar no debe deshacer ninguno de los dos
    /// en silencio. Si re-invitar *debería* restaurar a un miembro suspendido es una pregunta de
    /// producto, abierta como SDD-OD-13 — hasta que se responda, rechazar es la respuesta segura.
    /// </summary>
    [Fact]
    public void ReinviteRejectsASuspendedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        membership.Suspend(InvitedAt + TimeSpan.FromHours(2));

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                ["advisor"],
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Suspended, membership.State);
    }

    [Fact]
    public void ReinviteRejectsARemovedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        membership.Remove(InvitedAt + TimeSpan.FromHours(2));

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                ["advisor"],
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Removed, membership.State);
    }

    /// <summary>
    /// Una re-invitación rechazada tiene que dejar el agregado exactamente como estaba: rechazar
    /// después de haber reemplazado los roles repartiría permisos que nadie otorgó.
    /// </summary>
    [Fact]
    public void ARejectedReinviteLeavesRolesUntouched()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));

        Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                ["tenancy.admin"],
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal(["advisor"], membership.Roles);
    }

    /// <summary>
    /// AUTH-11. Suspender bloquea el acceso sin descartar la membresía, pero hasta ahora no
    /// había forma de volver: `Reinvite` rechaza `Suspended` y no existía operación propia.
    /// El owner eligió una operación separada y no que re-invitar restaure, porque son dos
    /// intenciones distintas — ver `SDD-OD-13`.
    /// </summary>
    [Fact]
    public void ReactivateReturnsASuspendedMembershipToActive()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        membership.Suspend(InvitedAt + TimeSpan.FromHours(2));
        var versionWhileSuspended = membership.Version;
        membership.PullDomainEvents();

        membership.Reactivate(InvitedAt + TimeSpan.FromHours(3));

        Assert.Equal(MembershipState.Active, membership.State);
        Assert.True(membership.Version > versionWhileSuspended);
        var domainEvent = Assert.Single(membership.DomainEvents);
        var reactivated = Assert.IsType<MembershipReactivatedDomainEvent>(domainEvent);
        Assert.Equal(membership.Id, reactivated.MembershipId);
        Assert.Equal(membership.UserId, reactivated.UserId);
    }

    /// <summary>
    /// La persona ya aceptó su invitación una vez: `AcceptedAt` se conserva. Volver a
    /// pedirle que acepte sería pedirle que confirme algo que ya confirmó.
    /// </summary>
    [Fact]
    public void ReactivateKeepsTheOriginalAcceptance()
    {
        var membership = Invite(Guid.CreateVersion7());
        var acceptedAt = InvitedAt + TimeSpan.FromHours(1);
        membership.Accept(acceptedAt);
        membership.Suspend(InvitedAt + TimeSpan.FromHours(2));

        membership.Reactivate(InvitedAt + TimeSpan.FromHours(3));

        Assert.Equal(acceptedAt, membership.AcceptedAt);
    }

    [Theory]
    [InlineData(MembershipState.Active)]
    [InlineData(MembershipState.Invited)]
    [InlineData(MembershipState.Removed)]
    public void ReactivateRejectsEveryStateThatIsNotSuspended(MembershipState state)
    {
        var membership = Invite(Guid.CreateVersion7());
        if (state is MembershipState.Active or MembershipState.Removed)
        {
            membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        }

        if (state == MembershipState.Removed)
        {
            membership.Remove(InvitedAt + TimeSpan.FromHours(2));
        }

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reactivate(InvitedAt + TimeSpan.FromHours(3)));

        Assert.Equal("tenancy.membership.not_reactivatable", error.Code);
        Assert.Equal(state, membership.State);
    }

    /// <summary>
    /// Reactivar no revive una invitación vencida: eso es re-invitar, y tiene su propio
    /// camino. Mezclarlos borraría la diferencia entre "se le venció el plazo" y "alguien
    /// decidió suspenderla".
    /// </summary>
    [Fact]
    public void ReactivateRejectsAnExpiredMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);
        Assert.True(membership.Expire(lapsed));

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reactivate(lapsed + TimeSpan.FromHours(1)));

        Assert.Equal("tenancy.membership.not_reactivatable", error.Code);
        Assert.Equal(MembershipState.Expired, membership.State);
    }

    private static Membership Invite(Guid userId) =>
        Membership.Invite(
            MembershipId.New(),
            userId,
            TenantId.New(),
            ["advisor"],
            "invitation",
            InvitedAt,
            Ttl);
}
