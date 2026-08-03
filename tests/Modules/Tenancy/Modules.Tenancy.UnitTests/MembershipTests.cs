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
