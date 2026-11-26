namespace Modules.Wms.Domain.Tasks;

/// <summary>Por qué un operario suelta una tarea (spec 2026-11-26 §6.7). Se persiste por nombre; no se renombra.</summary>
public enum TaskUnclaimReason
{
    ShiftEnd,
    CannotReach,
    Skipped,
    Other,
}
