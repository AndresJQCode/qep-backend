namespace Modules.Wms.Domain.Tasks;

/// <summary>Qué pasó en la historia de una tarea (spec 2026-11-26 §6.12). Se persiste por nombre; no se renombra.</summary>
public enum TaskEventKind
{
    Created,
    Published,
    Held,
    Assigned,
    Unassigned,
    Claimed,
    Unclaimed,
    LeaseExpired,
    LeaseResumed,
    PriorityChanged,
    LineConfirmed,
    LineCancelled,
    Completed,
    Cancelled,
}
