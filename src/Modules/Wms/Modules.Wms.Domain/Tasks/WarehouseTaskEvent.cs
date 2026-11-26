namespace Modules.Wms.Domain.Tasks;

/// <summary>Un hecho de la historia de una tarea (spec 2026-11-26 §6.1). Sólo se inserta: no se edita ni se borra.</summary>
public sealed class WarehouseTaskEvent
{
    private WarehouseTaskEvent()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    public TaskEventKind Kind { get; private set; }

    /// <summary>La membresía que lo hizo; <c>null</c> es el sistema.</summary>
    public Guid? MemberId { get; private set; }

    public string? DeviceId { get; private set; }

    public Guid? LineId { get; private set; }

    public decimal? Quantity { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static WarehouseTaskEvent Record(
        Guid tenantId,
        Guid taskId,
        TaskEventKind kind,
        Guid? memberId,
        string? deviceId,
        Guid? lineId,
        decimal? quantity,
        string? detail,
        DateTimeOffset at) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TaskId = taskId,
            Kind = kind,
            MemberId = memberId,
            DeviceId = deviceId,
            LineId = lineId,
            Quantity = quantity,
            Detail = detail,
            OccurredAt = at,
        };
}
