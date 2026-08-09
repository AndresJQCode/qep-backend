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
            [" tenancy.owner ", "tenancy.owner", "tenancy.member"],
            InvitedAt.AddHours(1));

        Assert.Equal(["tenancy.owner", "tenancy.member"], membership.Roles);
        var domainEvent = Assert.Single(membership.DomainEvents);
        var changed = Assert.IsType<MembershipRolesChangedDomainEvent>(domainEvent);
        Assert.Equal(["tenancy.member"], changed.PreviousRoles);
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
            membership.ChangeRoles(["tenancy.owner"], InvitedAt.AddHours(2)));

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
    /// AUTH-05 / SDD-OD-04. An invitation that lapses without the person ever trying to
    /// sign in stays in <see cref="MembershipState.Invited"/> with a past ExpiresAt,
    /// because expiry is lazy: only Accept transitions it. Re-inviting used to return that
    /// dead row unchanged, leaving the person permanently un-invitable while the admin
    /// believed the invitation had been sent.
    ///
    /// Renewal happens in place, not by inserting a second row: (UserId, TenantId) is a
    /// UNIQUE index (TenancyDbContext.cs:105), so one user has exactly one membership per
    /// tenant. See SDD-CT-15.
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

        membership.Reinvite(["tenancy.member"], lapsed, Ttl);

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

        membership.Reinvite(["tenancy.member"], lapsed, Ttl);

        Assert.Equal(MembershipState.Invited, membership.State);
        Assert.Equal(lapsed + Ttl, membership.ExpiresAt);
    }

    /// <summary>
    /// CA-AUTH-05-12: a live invitation is not disturbed. Renewing one would silently
    /// extend a window somebody is counting on, and invalidate the link already sent.
    /// </summary>
    [Fact]
    public void ReinviteRejectsAStillValidInvitation()
    {
        var membership = Invite(Guid.CreateVersion7());
        var withinWindow = InvitedAt + TimeSpan.FromHours(1);

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(["tenancy.member"], withinWindow, Ttl));

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
                ["tenancy.member"],
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Active, membership.State);
    }

    /// <summary>
    /// The security boundary the AUTH-05 review found untested. Suspending and removing are
    /// deliberate acts by an administrator; re-inviting must not undo either of them
    /// silently. Whether re-inviting *should* restore a suspended member is a product
    /// question, open as SDD-OD-13 — until it is answered, refusing is the safe answer.
    /// </summary>
    [Fact]
    public void ReinviteRejectsASuspendedMembership()
    {
        var membership = Invite(Guid.CreateVersion7());
        membership.Accept(InvitedAt + TimeSpan.FromHours(1));
        membership.Suspend(InvitedAt + TimeSpan.FromHours(2));

        var error = Assert.Throws<TenantDomainException>(
            () => membership.Reinvite(
                ["tenancy.member"],
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
                ["tenancy.member"],
                InvitedAt + Ttl + TimeSpan.FromHours(1),
                Ttl));

        Assert.Equal("tenancy.membership.not_reinvitable", error.Code);
        Assert.Equal(MembershipState.Removed, membership.State);
    }

    /// <summary>
    /// A rejected re-invitation must leave the aggregate exactly as it was: refusing while
    /// having already replaced the roles would hand out permissions nobody granted.
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

        Assert.Equal(["tenancy.member"], membership.Roles);
    }

    private static Membership Invite(Guid userId) =>
        Membership.Invite(
            MembershipId.New(),
            userId,
            TenantId.New(),
            ["tenancy.member"],
            "invitation",
            InvitedAt,
            Ttl);
}
