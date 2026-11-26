namespace Modules.Wms.Domain.Tasks;

/// <summary>Estado de una línea de tarea. Se persiste por nombre; no se renombra.</summary>
public enum TaskLineStatus
{
    Open,
    Done,
    Cancelled,
}
