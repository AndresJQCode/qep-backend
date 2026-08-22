using BuildingBlocks.Domain;

namespace Modules.Tenancy.Domain;

/// <summary>
/// La relación explícita entre un usuario y un tenant. Según el ADR 0016 el agregado
/// Membership pertenece a Tenancy: guarda la referencia al usuario sólo por id, su estado
/// de ciclo de vida, las referencias de rol (que resuelve Authorization) y fechas de auditoría.
/// Invitar a un usuario crea la Membership en <see cref="MembershipState.Invited"/>;
/// el primer login externo que coincida la acepta y la pasa a <see cref="MembershipState.Active"/>.
/// </summary>
public sealed class Membership
{
    /// <summary>Ventana de invitación por defecto, según el ADR 0016 (72 horas).</summary>
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
    /// Crea una membresía invitada que vence en <paramref name="invitedAt"/> más
    /// <paramref name="timeToLive"/> (72 horas por defecto, según el ADR 0016).
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
    /// Crea una membresía que ya está en <see cref="MembershipState.Active"/>. Sirve para
    /// dar de alta al owner de un tenant auto-registrado (ADR 0017), la única
    /// excepción al ciclo de invitar-y-luego-aceptar.
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
    /// Acepta una membresía invitada y la pasa a
    /// <see cref="MembershipState.Active"/>. Es idempotente para una membresía que ya
    /// está activa. Rechaza aceptar una invitación vencida (y la pasa a
    /// <see cref="MembershipState.Expired"/>) o un estado que no sea invitado ni activo.
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
    /// Vence una membresía invitada cuya ventana de invitación ya pasó. No hace nada en
    /// cualquier otro estado ni antes del vencimiento.
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
    /// Renueva en el lugar una invitación vencida, con ventana nueva y los roles que se den ahora.
    /// </summary>
    /// <remarks>
    /// En el lugar, y no creando una segunda membresía: (UserId, TenantId) es un índice
    /// UNIQUE, así que un usuario tiene exactamente una membresía por tenant. Ver SDD-CT-15.
    ///
    /// Existe porque el vencimiento es perezoso: sólo <see cref="Accept"/> pasa una invitación
    /// a <see cref="MembershipState.Expired"/>, y eso ocurre únicamente cuando la persona
    /// intenta entrar. Quien nunca lo intenta queda en <see cref="MembershipState.Invited"/>
    /// con un ExpiresAt en el pasado para siempre, y volver a invitarla devolvía esa fila
    /// muerta sin tocar: ni invitación nueva, ni error, ni aviso. Ver SDD-OD-04.
    ///
    /// Una invitación todavía válida se rechaza en vez de renovarse: extender una ventana viva
    /// invalida en silencio el link que ya está en la bandeja de alguien.
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
    /// Suspende una membresía activa, bloqueando el acceso sin descartarla.
    /// Sólo es válido desde <see cref="MembershipState.Active"/>; en la v1 no hay
    /// camino de reactivación (según el ADR 0016, los estados son transiciones reales y
    /// auditadas — un miembro suspendido tiene que ser re-invitado para volver).
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
    /// Devuelve una membresía suspendida a <see cref="MembershipState.Active"/>.
    /// </summary>
    /// <remarks>
    /// Sólo desde <see cref="MembershipState.Suspended"/>. No desde `Expired`, que es una
    /// invitación vencida y le corresponde a `Reinvite`: mezclarlos borraría la diferencia
    /// entre "se le venció el plazo" y "alguien decidió suspenderla".
    ///
    /// `AcceptedAt` se conserva intacto. La persona ya aceptó su invitación una vez, y
    /// pedirle que acepte de nuevo sería pedirle que confirme lo que ya confirmó.
    ///
    /// Lo agregó AUTH-11. Hasta entonces una suspensión era un callejón sin salida — `Reinvite`
    /// rechaza una membresía suspendida y nada más podía moverla — así que alguien suspendido
    /// por error no tenía forma de volver desde el producto. El owner eligió una operación
    /// separada en vez de que re-invitar restaure, porque son dos intenciones distintas: si
    /// invitar también restaurara, un administrador podría deshacer la suspensión que puso
    /// otro sin llegar a darse cuenta. Ver SDD-OD-13.
    /// </remarks>
    public void Reactivate(DateTimeOffset occurredAt)
    {
        if (State != MembershipState.Suspended)
        {
            throw new TenantDomainException(
                "tenancy.membership.not_reactivatable",
                "Only a suspended membership can be reactivated.");
        }

        State = MembershipState.Active;
        Version++;
        UpdatedAt = occurredAt;
        _domainEvents.Add(new MembershipReactivatedDomainEvent(
            Guid.CreateVersion7(),
            occurredAt,
            Id,
            TenantId,
            UserId));
    }

    /// <summary>
    /// Quita una membresía, revocándola de forma permanente. Válido desde cualquier
    /// estado no terminal (<see cref="MembershipState.Invited"/>,
    /// <see cref="MembershipState.Active"/> o <see cref="MembershipState.Suspended"/>).
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
