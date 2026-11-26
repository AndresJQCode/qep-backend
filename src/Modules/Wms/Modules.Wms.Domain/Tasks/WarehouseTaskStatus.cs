namespace Modules.Wms.Domain.Tasks;

/// <summary>Estado de una tarea (spec 2026-11-26 §6.3). Se persiste por nombre; no se renombra. <c>Blocked</c> y <c>NeedsRecovery</c>
/// entran con su corte.</summary>
public enum WarehouseTaskStatus
{
    Planned,
    Ready,
    Assigned,
    InProgress,
    Done,
    Cancelled,
}
