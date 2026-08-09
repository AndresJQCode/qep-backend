using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// The explicit relation between a user and a tenant. Per ADR 0016 the Membership
/// aggregate is owned by Tenancy: it holds the user reference by id only, its
/// lifecycle state, role references (resolved by Authorization) and audit dates.
/// Inviting a user creates the Membership in <see cref="MembershipState.Invited"/>;
/// the first matching external login accepts it into <see cref="MembershipState.Active"/>.
/// </summary>
public sealed class Membership
{
    /// <summary>Default invitation window, per ADR 0016 (72 hours).</summary>
    public static readonly TimeSpan DefaultInvitationTimeToLive = TimeSpan.FromHours(72);

    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly List<string> _roles = [];

    private Membership()
    {
    }

    private Membership(
        MembershipId id,
        Guid userId,
        TenantId tenantId,
        IEnumerable<string> roles,
        string origin,
        DateTimeOffset invitedAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        TenantId = tenantId;
        _roles.AddRange(NormalizeRoles(roles));
        Origin = ValidateOrigin(origin);
        State = MembershipState.Invited;
        InvitedAt = invitedAt;
        ExpiresAt = expiresAt;
        Version = 1;
        CreatedAt = invitedAt;
        UpdatedAt = invitedAt;
    }

    public MembershipId Id { get; private set; }

    public Guid UserId { get; private set; }

    public TenantId TenantId { get; private set; }

    public MembershipState State { get; private set; }

    public string Origin { get; private set; } = string.Empty;

    public DateTimeOffset InvitedAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<string> Roles => _roles.AsReadOnly();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Creates an invited membership that expires at <paramref name="invitedAt"/> plus
    /// <paramref name="timeToLive"/> (72 hours by default, per ADR 0016).
    /// </summary>
    public static Membership Invite(
        MembershipId id,
        Guid userId,
        TenantId tenantId,
        IEnumerable<string> roles,
        string origin,
        DateTimeOffset invitedAt,
        TimeSpan timeToLive)
    {
        if (userId == Guid.Empty)
        {
            throw new TenantDomainException(
                "tenancy.membership.user_required",
                "A membership requires a user reference.");
        }

        if (timeToLive <= TimeSpan.Zero)
        {
            throw new TenantDomainException(
                "tenancy.membership.ttl_invalid",
                "Invitation time-to-live must be positive.");
        }

        var membership = new Membership(
            id,
            userId,
            tenantId,
            roles,
            origin,
            invitedAt,
            invitedAt + timeToLive);
        membership._domainEvents.Add(new MembershipInvitedDomainEvent(
            Guid.CreateVersion7(),
            invitedAt,
            membership.Id,
            membership.TenantId,
            membership.UserId,
            membership.ExpiresAt));
        return membership;
    }

    /// <summary>
    /// Creates a membership already in <see cref="MembershipState.Active"/>. Used to
    /// bootstrap the owner of a self-registered tenant (ADR 0017), the single
    /// exception to the invite-then-accept lifecycle.
    /// </summary>
    public static Membership CreateActive(
        MembershipId id,
        Guid userId,
        TenantId tenantId,
        IEnumerable<string> roles,
        string origin,
        DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty)
        {
            throw new TenantDomainException(
                "tenancy.membership.user_required",
                "A membership requires a user reference.");
        }

        var membership = new Membership(
            id,
            userId,
            tenantId,
            roles,
            origin,
            createdAt,
            createdAt)
        {
            State = MembershipState.Active,
            AcceptedAt = createdAt,
        };
        membership._domainEvents.Add(new MembershipAcceptedDomainEvent(
            Guid.CreateVersion7(),
            createdAt,
            membership.Id,
            membership.TenantId,
            membership.UserId));
        return membership;
    }

    /// <summary>
    /// Accepts an invited membership, transitioning it to
    /// <see cref="MembershipState.Active"/>. Idempotent for an already-active
    /// membership. Rejects acceptance of an expired invitation (transitioning it to
    /// <see cref="MembershipState.Expired"/>) or of a non-invited, non-active state.
    /// </summary>
    public void Accept(DateTimeOffset occurredAt)
    {
        if (State == MembershipState.Active)
        {
            return;
        }

        if (State != MembershipState.Invited)
        {
            throw new TenantDomainException(
                "tenancy.membership.not_invited",
                "Only an invited membership can be accepted.");
        }

        if (occurredAt > ExpiresAt)
        {
            Expire(occurredAt);
            throw new TenantDomainException(
                "tenancy.membership.invitation_expired",
                "The invitation has expired.");
        }

        State = MembershipState.Active;
        AcceptedAt = occurredAt;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new MembershipAcceptedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            TenantId,
            UserId));
    }

    /// <summary>
    /// Expires an invited membership whose invitation window has elapsed. No-op for
    /// any other state or before expiry.
    /// </summary>
    public bool Expire(DateTimeOffset occurredAt)
    {
        if (State != MembershipState.Invited || occurredAt <= ExpiresAt)
        {
            return false;
        }

        State = MembershipState.Expired;
        Version++;
        UpdatedAt = occurredAt;
        return true;
    }

    /// <summary>
    /// Renews a lapsed invitation in place, with a fresh window and the roles given now.
    /// </summary>
    /// <remarks>
    /// In place, and not by creating a second membership: (UserId, TenantId) is a UNIQUE
    /// index, so one user holds exactly one membership per tenant. See SDD-CT-15.
    ///
    /// Exists because expiry is lazy — only <see cref="Accept"/> transitions an invitation
    /// to <see cref="MembershipState.Expired"/>, and that only happens when the person
    /// tries to sign in. Someone who never tries stays <see cref="MembershipState.Invited"/>
    /// with a ExpiresAt in the past forever, and re-inviting them used to return that dead
    /// row untouched: no new invitation, no failure, no warning. See SDD-OD-04.
    ///
    /// A still-valid invitation is refused rather than renewed: extending a live window
    /// silently invalidates the link already in someone's inbox.
    /// </remarks>
    public void Reinvite(
        IEnumerable<string> roles,
        DateTimeOffset occurredAt,
        TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero)
        {
            throw new TenantDomainException(
                "tenancy.membership.ttl_invalid",
                "Invitation time-to-live must be positive.");
        }

        if (State == MembershipState.Invited && occurredAt <= ExpiresAt)
        {
            throw new TenantDomainException(
                "tenancy.membership.invitation_still_valid",
                "The invitation has not expired yet.");
        }

        if (State is not (MembershipState.Invited or MembershipState.Expired))
        {
            throw new TenantDomainException(
                "tenancy.membership.not_reinvitable",
                "Only a lapsed or expired invitation can be re-invited.");
        }

        _roles.Clear();
        _roles.AddRange(NormalizeRoles(roles));
        State = MembershipState.Invited;
        InvitedAt = occurredAt;
        ExpiresAt = occurredAt + timeToLive;
        AcceptedAt = null;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new MembershipInvitedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            TenantId,
            UserId,
            ExpiresAt));
    }

    /// <summary>
    /// Suspends an active membership, blocking access without discarding it.
    /// Only valid from <see cref="MembershipState.Active"/>; there is no
    /// reactivation path in v1 (per ADR 0016, states are real, audited
    /// transitions — a suspended member must be re-invited to return).
    /// </summary>
    public void Suspend(DateTimeOffset occurredAt)
    {
        if (State != MembershipState.Active)
        {
            throw new TenantDomainException(
                "tenancy.membership.not_active",
                "Only an active membership can be suspended.");
        }

        State = MembershipState.Suspended;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new MembershipSuspendedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            TenantId,
            UserId));
    }

    /// <summary>
    /// Removes a membership, permanently revoking it. Valid from any
    /// non-terminal state (<see cref="MembershipState.Invited"/>,
    /// <see cref="MembershipState.Active"/> or <see cref="MembershipState.Suspended"/>).
    /// </summary>
    public void Remove(DateTimeOffset occurredAt)
    {
        if (State is MembershipState.Removed or MembershipState.Expired)
        {
            throw new TenantDomainException(
                "tenancy.membership.already_terminal",
                "The membership is already removed or expired.");
        }

        State = MembershipState.Removed;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new MembershipRemovedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            TenantId,
            UserId));
    }

    public void ChangeRoles(IEnumerable<string> roles, DateTimeOffset occurredAt)
    {
        if (State is MembershipState.Removed or MembershipState.Expired)
        {
            throw new TenantDomainException(
                "tenancy.membership.already_terminal",
                "Roles cannot be changed for a removed or expired membership.");
        }

        var normalizedRoles = NormalizeRoles(roles).ToArray();
        if (normalizedRoles.Length == 0)
        {
            throw new TenantDomainException(
                "tenancy.membership.roles_required",
                "A membership requires at least one role.");
        }

        if (_roles.SequenceEqual(normalizedRoles, StringComparer.Ordinal))
        {
            return;
        }

        var previousRoles = _roles.ToArray();
        _roles.Clear();
        _roles.AddRange(normalizedRoles);
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new MembershipRolesChangedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            TenantId,
            UserId,
            previousRoles,
            normalizedRoles));
    }

    public IReadOnlyCollection<IDomainEvent> PullDomainEvents()
    {
        var events = _domainEvents.ToArray();
        _domainEvents.Clear();
        return events;
    }

    private static IEnumerable<string> NormalizeRoles(IEnumerable<string> roles) =>
        roles
            .Select(role => role.Trim())
            .Where(role => role.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static string ValidateOrigin(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 50)
        {
            throw new TenantDomainException(
                "tenancy.membership.origin_invalid",
                "Membership origin must be a non-empty value of at most 50 characters.");
        }

        return normalized;
    }
}
