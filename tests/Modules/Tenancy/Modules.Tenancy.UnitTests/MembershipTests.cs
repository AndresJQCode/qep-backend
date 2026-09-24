using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class MembershipTests
{
    private static readonly DateTimeOffset InvitedAt =
        new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Ttl = Membership.DefaultInvitationTimeToLive;

    // El agregado no genera ni hashea: recibe el par ya resuelto desde Application
    // (InvitationTokens), así que acá alcanza con valores distinguibles entre sí.
    private const string Token = "plain-invitation-token";
    private const string TokenHash = "plain-invitation-token-hash";
    private const string RenewedToken = "renewed-invitation-token";
    private const string RenewedTokenHash = "renewed-invitation-token-hash";
    private const string InvitedName = "Ana Pérez";

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

        membership.Suspend(TenantOwnedByAnother(), InvitedAt.AddHours(2));

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
            membership.Suspend(TenantOwnedByAnother(), InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.not_active", exception.Code);
        Assert.Equal(MembershipState.Invited, membership.State);
    }

    [Fact]
    public void RemoveFromInvitedTransitionsToRemovedAndRaisesEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

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
        membership.Suspend(TenantOwnedByAnother(), InvitedAt.AddHours(2));

        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(3));

        Assert.Equal(MembershipState.Removed, membership.State);
    }

    [Fact]
    public void RemoveAlreadyRemovedThrows()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(2)));

        Assert.Equal("tenancy.membership.already_terminal", exception.Code);
    }

    [Fact]
    public void RemoveExpiredThrows()
    {
        var membership = Invite(Guid.CreateVersion7());
        Assert.True(membership.Expire(InvitedAt + Ttl + TimeSpan.FromSeconds(1)));

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Remove(TenantOwnedByAnother(), InvitedAt + Ttl + TimeSpan.FromHours(1)));

        Assert.Equal("tenancy.membership.already_terminal", exception.Code);
    }

    /// <summary>
    /// El owner es la última autoridad del tenant (ADR 0017) y su membresía no se puede
    /// suspender ni quitar, por nadie: la marca es el Origin de registro, porque el tenant
    /// no guarda referencia a quién lo creó.
    /// </summary>
    [Fact]
    public void SuspendOwnerMembershipThrows()
    {
        var membership = CreateOwner();
        var tenant = TenantOwnedBy(membership);
        membership.PullDomainEvents();

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Suspend(tenant, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.owner_protected", exception.Code);
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Empty(membership.DomainEvents);
    }

    [Fact]
    public void RemoveOwnerMembershipThrows()
    {
        var membership = CreateOwner();
        var tenant = TenantOwnedBy(membership);
        membership.PullDomainEvents();

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.Remove(tenant, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.owner_protected", exception.Code);
        Assert.Equal(MembershipState.Active, membership.State);
        Assert.Empty(membership.DomainEvents);
    }

    /// <summary>
    /// Una membresía con <c>Origin = registration</c> que el tenant **no** nombró owner se
    /// suspende como cualquier otra. Es el caso que distingue las dos preguntas: `origin` dice
    /// cómo nació la membresía, `tenants.owner_membership_id` dice quién manda. Antes de este
    /// cambio el sembrador tenía que reusar el origen de registro para heredar la protección
    /// (TenancySeeder.cs), y cualquier fila con ese origen quedaba blindada sin que nadie lo
    /// hubiera decidido.
    /// </summary>
    [Fact]
    public void SuspendMembershipWithRegistrationOriginThatIsNotTheOwnerSucceeds()
    {
        var membership = CreateOwner();
        membership.PullDomainEvents();

        membership.Suspend(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        Assert.Equal(MembershipState.Suspended, membership.State);
        Assert.IsType<MembershipSuspendedDomainEvent>(Assert.Single(membership.DomainEvents));
    }

    [Fact]
    public void ChangeRolesNormalizesRolesAndRaisesEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.ChangeRoles(TenantOwnedByAnother(), 
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
            membership.ChangeRoles(TenantOwnedByAnother(), ["  "], InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.roles_required", exception.Code);
    }

    [Fact]
    public void ChangeRolesCannotRemoveAdminFromOwnerMembership()
    {
        var membership = CreateOwner();
        var tenant = TenantOwnedBy(membership);
        membership.PullDomainEvents();

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.ChangeRoles(tenant, ["advisor"], InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.owner_protected", exception.Code);
        Assert.Equal(["admin"], membership.Roles);
    }

    [Fact]
    public void ChangeRolesOnOwnerMembershipAllowsExtraRolesWhileKeepingAdmin()
    {
        var membership = CreateOwner();
        membership.PullDomainEvents();

        membership.ChangeRoles(TenantOwnedByAnother(), ["admin", "advisor"], InvitedAt.AddHours(1));

        Assert.Equal(["admin", "advisor"], membership.Roles);
    }

    [Fact]
    public void ChangeRolesForRemovedMembershipThrows()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        var exception = Assert.Throws<TenantDomainException>(() =>
            membership.ChangeRoles(TenantOwnedByAnother(), ["admin"], InvitedAt.AddHours(2)));

        Assert.Equal("tenancy.membership.already_terminal", exception.Code);
    }

    /// <summary>
    /// El token plano viaja únicamente en el evento de dominio (outbox → email); en el
    /// agregado queda sólo el hash. Guardar el token en la fila lo dejaría legible para
    /// cualquiera con acceso a la base, que es lo que el hash viene a impedir.
    /// </summary>
    [Fact]
    public void InviteStoresOnlyTheTokenHashAndTheEventCarriesThePlainToken()
    {
        var membership = Invite(Guid.CreateVersion7());

        Assert.Equal(TokenHash, membership.InvitationTokenHash);
        var invited = Assert.IsType<MembershipInvitedDomainEvent>(
            Assert.Single(membership.DomainEvents));
        Assert.Equal(Token, invited.Token);
    }

    [Fact]
    public void InviteRequiresAnInvitationToken()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.CreateVersion7(),
                TenantId.New(),
                InvitedName,
                ["advisor"],
                "invitation",
                " ",
                TokenHash,
                InvitedAt,
                Ttl));

        Assert.Equal("tenancy.membership.invitation_token_required", exception.Code);
    }

    /// <summary>
    /// Renovar rota el token: el link vencido que quedó en la bandeja no puede seguir
    /// siendo válido, y el email nuevo lleva el token nuevo vía el evento re-emitido.
    /// </summary>
    [Fact]
    public void ReinviteRotatesTheInvitationToken()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

        Assert.Equal(RenewedTokenHash, membership.InvitationTokenHash);
        var invited = Assert.IsType<MembershipInvitedDomainEvent>(
            Assert.Single(membership.DomainEvents));
        Assert.Equal(RenewedToken, invited.Token);
    }

    /// <summary>
    /// El hash sobrevive a la aceptación: GET /invitations/{token} tiene que poder
    /// responder "ya aceptada" y el accept repetido ser idempotente por el mismo link.
    /// </summary>
    [Fact]
    public void AcceptKeepsTheInvitationTokenHash()
    {
        var membership = Invite(Guid.CreateVersion7());

        membership.Accept(InvitedAt.AddHours(1));

        Assert.Equal(TokenHash, membership.InvitationTokenHash);
    }

    [Fact]
    public void CreateActiveHasNoInvitationToken()
    {
        // El owner de un tenant auto-registrado nunca fue invitado: no hay link que honrar.
        Assert.Null(CreateOwner().InvitationTokenHash);
    }

    [Fact]
    public void InviteWithEmptyUserThrows()
    {
        var exception = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.Empty,
                TenantId.New(),
                InvitedName,
                [],
                "invitation",
                Token,
                TokenHash,
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

        membership.Reinvite(InvitedName, ["tenancy.admin"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

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

        membership.Reinvite(InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

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

        membership.Reinvite(InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

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
            () => membership.Reinvite(
                InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, withinWindow, Ttl));

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
                InvitedName,
                ["advisor"],
                RenewedToken,
                RenewedTokenHash,
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Active, membership.State);
    }

    /// <summary>
    /// La frontera de seguridad que la revisión de AUTH-05 encontró sin probar. Suspender es un
    /// acto deliberado de un administrador, y re-invitar no debe deshacerlo en silencio: la
    /// suspensión se levanta con Reactivate, una operación separada (SDD-OD-13). Quitar ya no
    /// entra acá: una membresía quitada sí se re-invita, pero vuelve a Invited y no a Active, así
    /// que nadie recupera acceso sin aceptar de nuevo (ReinviteRenewsARemovedMembership).
    /// </summary>
    [Fact]
    public void ReinviteRejectsASuspendedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        membership.Suspend(TenantOwnedByAnother(), InvitedAt + TimeSpan.FromHours(2));

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                InvitedName,
                ["advisor"],
                RenewedToken,
                RenewedTokenHash,
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Suspended, membership.State);
    }

    /// <summary>
    /// Quitar a alguien no le cierra la puerta para siempre: el owner decidió que una persona
    /// quitada se puede volver a invitar. Se renueva igual que una invitación vencida —en el
    /// lugar, con ventana, roles, nombre y token nuevos— y vuelve a
    /// <see cref="MembershipState.Invited"/>, así que tiene que aceptar otra vez.
    ///
    /// Se re-invita dentro de la ventana original a propósito: la regla de la invitación viva
    /// mira sólo las filas en Invited, y una membresía quitada no tiene un link vigente que cuidar.
    /// </summary>
    [Fact]
    public void ReinviteRenewsARemovedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        var originalId = membership.Id;
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        membership.Remove(TenantOwnedByAnother(), InvitedAt + TimeSpan.FromHours(2));
        var versionWhileRemoved = membership.Version;
        membership.PullDomainEvents();
        var renewedAt = InvitedAt + TimeSpan.FromHours(3);

        membership.Reinvite(
            "Ana María Pérez", ["tenancy.admin"], RenewedToken, RenewedTokenHash, renewedAt, Ttl);

        Assert.Equal(originalId, membership.Id);
        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(renewedAt, membership.InvitedAt);
        Assert.Equal(renewedAt + Ttl, membership.ExpiresAt);
        Assert.Null(membership.AcceptedAt);
        Assert.Equal(["tenancy.admin"], membership.Roles);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
        Assert.Equal(RenewedTokenHash, membership.InvitationTokenHash);
        Assert.Equal(versionWhileRemoved + 1, membership.Version);
        Assert.Equal(renewedAt, membership.UpdatedAt);
        var invited = Assert.IsType<MembershipInvitedDomainEvent>(
            Assert.Single(membership.DomainEvents));
        Assert.Equal(membership.Id, invited.MembershipId);
        Assert.Equal(membership.ExpiresAt, invited.ExpiresAt);
        Assert.Equal(RenewedToken, invited.Token);
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
                InvitedName,
                ["tenancy.admin"],
                RenewedToken,
                RenewedTokenHash,
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal(["advisor"], membership.Roles);
        // Tampoco rota el token: el link que ya está en la bandeja tiene que seguir andando.
        Assert.Equal(TokenHash, membership.InvitationTokenHash);
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
        membership.Suspend(TenantOwnedByAnother(), InvitedAt + TimeSpan.FromHours(2));
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
        membership.Suspend(TenantOwnedByAnother(), InvitedAt + TimeSpan.FromHours(2));

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
            membership.Remove(TenantOwnedByAnother(), InvitedAt + TimeSpan.FromHours(2));
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

    /// <summary>
    /// El perfil —nombre y código de asesor— es presentación del tenant: vive en la membresía, se
    /// normaliza en el agregado y cambiarlo cuenta como cambio —sube la versión— porque el roster
    /// lo edita con If-Match.
    /// </summary>
    [Fact]
    public void UpdateProfileTrimsTheNameAndBumpsTheVersion()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;
        var updatedAt = InvitedAt.AddHours(1);

        var changed = membership.UpdateProfile("  Ana María Pérez  ", null, updatedAt);

        Assert.True(changed);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
        Assert.Equal(version + 1, membership.Version);
        Assert.Equal(updatedAt, membership.UpdatedAt);
    }

    // Guardar dos veces lo mismo no es un cambio: subir la versión invalidaría el If-Match de
    // otra pestaña por nada.
    [Fact]
    public void UpdateProfileWithTheSameNormalizedValuesIsANoOp()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        membership.UpdateProfile("Ana María Pérez", 12, InvitedAt.AddHours(1));
        var version = membership.Version;
        var updatedAt = membership.UpdatedAt;

        var changed = membership.UpdateProfile("  Ana María Pérez ", 12, InvitedAt.AddHours(2));

        Assert.False(changed);
        Assert.Equal(version, membership.Version);
        Assert.Equal(updatedAt, membership.UpdatedAt);
    }

    // Spec 2026-09-24: cambiar sólo el código es un cambio de perfil como cualquier otro.
    [Fact]
    public void ChangingOnlyTheAdvisorCodeBumpsTheVersion()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;

        var changed = membership.UpdateProfile(InvitedName, 12, InvitedAt.AddHours(1));

        Assert.True(changed);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(InvitedName, membership.DisplayName);
        Assert.Equal(version + 1, membership.Version);
    }

    // D1: el código es opcional también al editar; mandarlo nulo lo borra.
    [Fact]
    public void UpdateProfileWithANullCodeClearsIt()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var version = membership.Version;

        var changed = membership.UpdateProfile(InvitedName, null, InvitedAt.AddHours(1));

        Assert.True(changed);
        Assert.Null(membership.AdvisorCode);
        Assert.Equal(version + 1, membership.Version);
    }

    // D2: entero >= 1. Un rechazo no deja nada a medias: ni el nombre nuevo ni la versión.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void UpdateProfileRejectsANonPositiveCodeAndLeavesTheMembershipUntouched(int advisorCode)
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var version = membership.Version;

        var error = Assert.Throws<TenantDomainException>(
            () => membership.UpdateProfile("Ana María Pérez", advisorCode, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.advisor_code_invalid", error.Code);
        Assert.Equal(InvitedName, membership.DisplayName);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(version, membership.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateProfileRejectsABlankNameAndLeavesTheMembershipUntouched(string displayName)
    {
        var membership = Invite(Guid.CreateVersion7());
        var before = membership.DisplayName;
        var version = membership.Version;

        var error = Assert.Throws<TenantDomainException>(
            () => membership.UpdateProfile(displayName, 12, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
        Assert.Equal(before, membership.DisplayName);
        Assert.Null(membership.AdvisorCode);
        Assert.Equal(version, membership.Version);
    }

    [Fact]
    public void UpdateProfileRejectsANameLongerThanTheColumn()
    {
        var membership = Invite(Guid.CreateVersion7());

        var error = Assert.Throws<TenantDomainException>(() => membership.UpdateProfile(
            new string('a', Membership.DisplayNameMaxLength + 1), null, InvitedAt.AddHours(1)));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    [Fact]
    public void UpdateProfileAcceptsANameExactlyAsLongAsTheColumn()
    {
        var membership = Invite(Guid.CreateVersion7());
        var name = new string('a', Membership.DisplayNameMaxLength);

        membership.UpdateProfile(name, null, InvitedAt.AddHours(1));

        Assert.Equal(name, membership.DisplayName);
    }

    // El perfil no cambia el acceso, así que se puede cargar en cualquier estado.
    [Fact]
    public void UpdateProfileWorksOnASuspendedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt.AddHours(1));
        membership.Suspend(TenantOwnedByAnother(), InvitedAt.AddHours(2));

        Assert.True(membership.UpdateProfile("Ana María Pérez", 12, InvitedAt.AddHours(3)));
        Assert.Equal(MembershipState.Suspended, membership.State);
    }

    // El owner entra por register-tenant, no por invitación: nace sin nombre ni código y los
    // carga desde el roster. La protección de owner no alcanza al perfil.
    [Fact]
    public void TheOwnerStartsWithoutANameOrCodeAndCanGetThemLater()
    {
        var owner = CreateOwner();

        Assert.Null(owner.DisplayName);
        Assert.Null(owner.AdvisorCode);
        Assert.True(owner.UpdateProfile("Laura Gómez", 7, InvitedAt.AddHours(1)));
        Assert.Equal("Laura Gómez", owner.DisplayName);
        Assert.Equal(7, owner.AdvisorCode);
    }

    // Nadie consume un cambio de perfil fuera de Tenancy: el PDF y el Excel lo leen al generarse.
    [Fact]
    public void UpdateProfileRaisesNoDomainEvent()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.PullDomainEvents();

        membership.UpdateProfile("Ana María Pérez", 12, InvitedAt.AddHours(1));

        Assert.Empty(membership.DomainEvents);
    }

    [Fact]
    public void InviteStoresTheTrimmedDisplayName()
    {
        var membership = Membership.Invite(
            MembershipId.New(),
            Guid.CreateVersion7(),
            TenantId.New(),
            "  Ana Pérez  ",
            ["advisor"],
            "invitation",
            Token,
            TokenHash,
            InvitedAt,
            Ttl);

        Assert.Equal("Ana Pérez", membership.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InviteRejectsABlankDisplayName(string displayName)
    {
        var error = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.CreateVersion7(),
                TenantId.New(),
                displayName,
                ["advisor"],
                "invitation",
                Token,
                TokenHash,
                InvitedAt,
                Ttl));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    [Fact]
    public void InviteRejectsADisplayNameLongerThanTheColumn()
    {
        var error = Assert.Throws<TenantDomainException>(() =>
            Membership.Invite(
                MembershipId.New(),
                Guid.CreateVersion7(),
                TenantId.New(),
                new string('a', Membership.DisplayNameMaxLength + 1),
                ["advisor"],
                "invitation",
                Token,
                TokenHash,
                InvitedAt,
                Ttl));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
    }

    // D5: renovar una invitación vencida reescribe el nombre junto con los roles y la ventana.
    [Fact]
    public void ReinviteOverwritesTheDisplayName()
    {
        var membership = Invite(Guid.CreateVersion7());
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(
            "  Ana María Pérez  ", ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

        Assert.Equal("Ana María Pérez", membership.DisplayName);
    }

    // Mismo criterio que ARejectedReinviteLeavesRolesUntouched: un rechazo no deja nada a medias,
    // ni roles nuevos ni un token rotado.
    [Fact]
    public void AReinviteWithABlankNameLeavesTheMembershipUntouched()
    {
        var membership = Invite(Guid.CreateVersion7());
        var version = membership.Version;
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                "   ", ["tenancy.admin"], RenewedToken, RenewedTokenHash, lapsed, Ttl));

        Assert.Equal("tenancy.membership.display_name_invalid", error.Code);
        Assert.Equal(InvitedName, membership.DisplayName);
        Assert.Equal(["advisor"], membership.Roles);
        Assert.Equal(TokenHash, membership.InvitationTokenHash);
        Assert.Equal(version, membership.Version);
    }

    [Fact]
    public void InviteStoresTheAdvisorCode()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);

        Assert.Equal(12, membership.AdvisorCode);
    }

    // D1: opcional al invitar.
    [Fact]
    public void InviteWithoutAnAdvisorCodeLeavesItNull()
    {
        var membership = Invite(Guid.CreateVersion7());

        Assert.Null(membership.AdvisorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void InviteRejectsANonPositiveAdvisorCode(int advisorCode)
    {
        var error = Assert.Throws<TenantDomainException>(
            () => Invite(Guid.CreateVersion7(), advisorCode));

        Assert.Equal("tenancy.membership.advisor_code_invalid", error.Code);
    }

    // Spec 2026-09-24, Application y API: en la re-invitación un código en el cuerpo reemplaza al
    // que había.
    [Fact]
    public void ReinviteAppliesTheAdvisorCodeFromTheBody()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(
            InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl, advisorCode: 34);

        Assert.Equal(34, membership.AdvisorCode);
    }

    // A diferencia del nombre, un cuerpo sin código no borra el que había: la re-invitación lo
    // conserva (decisión del developer, 2026-09-24). Borrar el código es sólo por PUT .../profile.
    [Fact]
    public void ReinviteWithoutACodeKeepsThePreviousOne()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        membership.Reinvite(InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, lapsed, Ttl);

        Assert.Equal(12, membership.AdvisorCode);
    }

    // D4: la quitada que vuelve sin código en el cuerpo sigue con el suyo.
    [Fact]
    public void ReinvitingARemovedMembershipWithoutACodeKeepsIt()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        membership.Reinvite(
            InvitedName, ["advisor"], RenewedToken, RenewedTokenHash, InvitedAt.AddHours(2), Ttl);

        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(12, membership.AdvisorCode);
    }

    // Un código inválido corta antes de tocar nada: ni roles, ni token, ni versión.
    [Fact]
    public void AReinviteWithAnInvalidCodeLeavesTheMembershipUntouched()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);
        var version = membership.Version;
        var lapsed = InvitedAt + Ttl + TimeSpan.FromHours(1);

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                InvitedName, ["tenancy.admin"], RenewedToken, RenewedTokenHash, lapsed, Ttl,
                advisorCode: 0));

        Assert.Equal("tenancy.membership.advisor_code_invalid", error.Code);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(["advisor"], membership.Roles);
        Assert.Equal(TokenHash, membership.InvitationTokenHash);
        Assert.Equal(version, membership.Version);
    }

    // D4: la membresía quitada conserva su código.
    [Fact]
    public void RemovingAMembershipKeepsItsAdvisorCode()
    {
        var membership = Invite(Guid.CreateVersion7(), advisorCode: 12);

        membership.Remove(TenantOwnedByAnother(), InvitedAt.AddHours(1));

        Assert.Equal(MembershipState.Removed, membership.State);
        Assert.Equal(12, membership.AdvisorCode);
    }

    private static Membership Invite(Guid userId, int? advisorCode = null) =>
        Membership.Invite(
            MembershipId.New(),
            userId,
            TenantId.New(),
            InvitedName,
            ["advisor"],
            "invitation",
            Token,
            TokenHash,
            InvitedAt,
            Ttl,
            advisorCode);

    private static Membership CreateOwner() =>
        Membership.CreateActive(
            MembershipId.New(),
            Guid.CreateVersion7(),
            TenantId.New(),
            ["admin"],
            Membership.RegistrationOrigin,
            InvitedAt);

    /// <summary>
    /// El tenant que nombra owner a esta membresía: es lo que activa la guarda.
    /// </summary>
    private static Tenant TenantOwnedBy(Membership membership) => NewTenant(membership.Id);

    /// <summary>
    /// Un tenant cuya autoridad es otra membresía. Lo usan todos los casos que no son del owner,
    /// para que la guarda no se active por accidente y la prueba ejercite lo que dice ejercitar.
    /// </summary>
    private static Tenant TenantOwnedByAnother() => NewTenant(MembershipId.New());

    private static Tenant NewTenant(MembershipId owner) =>
        Tenant.Create(
            TenantId.New(),
            "qcode-demo",
            "QCode Demo",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            owner,
            InvitedAt);
}
