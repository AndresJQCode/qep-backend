namespace Modules.Scheduling.Domain;

/// <summary>
/// Una sesión es <c>(recurso, servicio, inicio, fin, cupo, reservas)</c> y <b>toda cita pertenece a una</b>: una cita individual es una
/// sesión de cupo 1 (spec 2026-11-21, D-2 y D-8). Eso deja una sola regla en la base: dos sesiones activas del mismo recurso no pueden
/// solaparse en <c>[starts_at, occupies_until)</c>. Una sesión de cupo 1 o abierta por la primera reserva se desactiva al quedarse sin
/// citas y libera el tiempo; una publicada permanece activa.
/// </summary>
public sealed class Session : VersionedEntity
{
    private Session()
    {
    }

    public SessionId Id { get; private set; }

    public ResourceId ResourceId { get; private set; }

    public ServiceId ServiceId { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    /// <summary>El fin del rango que ocupa del recurso: <see cref="EndsAt"/> más el margen del servicio.</summary>
    public DateTimeOffset OccupiesUntil { get; private set; }

    public int Capacity { get; private set; }

    public int BookedCount { get; private set; }

    public bool IsPublished { get; private set; }

    public bool IsActive { get; private set; }

    public bool HasRoom => BookedCount < Capacity;

    public static Session Create(
        SessionId id,
        Guid tenantId,
        ResourceId resourceId,
        ServiceId serviceId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        int bufferMinutes,
        int capacity,
        bool isPublished,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferMinutes);
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("A session must end after it starts.", nameof(endsAt));
        }

        var session = new Session
        {
            Id = id,
            ResourceId = resourceId,
            ServiceId = serviceId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            OccupiesUntil = endsAt.AddMinutes(bufferMinutes),
            Capacity = capacity,
            IsPublished = isPublished,
            IsActive = true,
        };
        session.Initialize(tenantId, now);
        return session;
    }

    /// <summary>Aparta un cupo. Application revisa <see cref="HasRoom"/> primero para responder 409 <c>session_full</c>; este error es la
    /// defensa del dominio. Reservar una sesión que una reserva abrió y se vació la reabre; una publicada que el tenant quitó
    /// (<see cref="Deactivate"/>) no se reabre: sólo <see cref="Republish"/> la trae de vuelta.</summary>
    public void Reserve(DateTimeOffset now)
    {
        if (!IsActive && IsPublished)
        {
            throw new InvalidOperationException("A published session the tenant removed cannot be booked until it is published again.");
        }

        if (!HasRoom)
        {
            throw new SchedulingDomainException(SchedulingErrorCodes.SessionFull, "The session has no seats left.");
        }

        BookedCount++;
        IsActive = true;
        Touch(now);
    }

    /// <summary>
    /// The tenant stops publishing a session that has no active bookings but whose cancelled appointments still point at it (the foreign
    /// key forbids deleting the row). It stops occupying the resource (the exclusion constraint only covers <c>is_active</c> rows) and
    /// stays marked as published and inactive: that pair is what tells "the tenant removed it" apart from an ad-hoc session a booking
    /// opened and emptied, so a booking never reopens it (only <see cref="Republish"/> does).
    /// </summary>
    public void Deactivate(DateTimeOffset now)
    {
        if (BookedCount > 0)
        {
            throw new InvalidOperationException("A session with bookings cannot be deactivated.");
        }

        IsActive = false;
        Touch(now);
    }

    /// <summary>
    /// Publishing again on the same key (resource, service, start) brings the inactive row back instead of inserting a second one. The
    /// range and the seats are taken from the current service and block: both may have changed since the session was removed.
    /// </summary>
    public void Republish(DateTimeOffset endsAt, int bufferMinutes, int capacity, DateTimeOffset now)
    {
        if (IsActive || BookedCount > 0)
        {
            throw new InvalidOperationException("Only an inactive session without bookings can be published again.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferMinutes);
        if (endsAt <= StartsAt)
        {
            throw new ArgumentException("A session must end after it starts.", nameof(endsAt));
        }

        EndsAt = endsAt;
        OccupiesUntil = endsAt.AddMinutes(bufferMinutes);
        Capacity = capacity;
        IsPublished = true;
        IsActive = true;
        Touch(now);
    }

    public void Release(DateTimeOffset now)
    {
        if (BookedCount == 0)
        {
            throw new InvalidOperationException("The session has no booking to release.");
        }

        BookedCount--;
        if (BookedCount == 0 && !IsPublished)
        {
            IsActive = false;
        }

        Touch(now);
    }
}
