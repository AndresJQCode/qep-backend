namespace Modules.Wms.Domain.Control;

/// <summary>Un hecho de la historia de una novedad. Sólo se inserta: no se edita ni se borra. Los hechos del operario y del sistema
/// viven aquí y no en la auditoría (DA-50).</summary>
public sealed class IncidentEvent
{
    private IncidentEvent()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid IncidentId { get; private set; }

    public IncidentEventKind Kind { get; private set; }

    /// <summary>La membresía que lo hizo; <c>null</c> es el sistema.</summary>
    public Guid? MemberId { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Cuándo lo guardó el servidor; la historia se ordena por esta fecha. <see cref="OccurredAt"/> es la del hecho.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public static IncidentEvent Record(
        Guid tenantId,
        Guid incidentId,
        IncidentEventKind kind,
        Guid? memberId,
        string? detail,
        DateTimeOffset at,
        DateTimeOffset recordedAt) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            IncidentId = incidentId,
            Kind = kind,
            MemberId = memberId,
            Detail = detail,
            OccurredAt = at.ToUniversalTime(),
            RecordedAt = recordedAt.ToUniversalTime(),
        };
}
