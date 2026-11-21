namespace Modules.Scheduling.Domain;

/// <summary>
/// Un bloqueo: cierra un recurso (o toda la sede, con <see cref="ResourceId"/> nulo) en un rango de instantes. Es lo que el calendario
/// dibuja como «Bloqueado». Un bloqueo que cruza citas vigentes se rechaza hasta reprogramarlas (spec 2026-11-21, D-12); eso lo decide
/// Application, que conoce las citas.
/// </summary>
public sealed class Closure : VersionedEntity
{
    private Closure()
    {
    }

    public ClosureId Id { get; private set; }

    public LocationId LocationId { get; private set; }

    public ResourceId? ResourceId { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public string? Reason { get; private set; }

    public static Closure Create(
        ClosureId id,
        Guid tenantId,
        LocationId locationId,
        ResourceId? resourceId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string? reason,
        DateTimeOffset now)
    {
        if (endsAt <= startsAt)
        {
            throw new ArgumentException("A closure must end after it starts.", nameof(endsAt));
        }

        var closure = new Closure
        {
            Id = id,
            LocationId = locationId,
            ResourceId = resourceId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };
        closure.Initialize(tenantId, now);
        return closure;
    }

    public bool AppliesTo(ResourceId resourceId) => ResourceId is null || ResourceId == resourceId;

    /// <summary>Dos rangos que sólo se tocan en un extremo no se solapan.</summary>
    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartsAt < end && start < EndsAt;
}
