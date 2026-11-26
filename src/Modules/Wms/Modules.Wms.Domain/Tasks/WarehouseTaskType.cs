namespace Modules.Wms.Domain.Tasks;

/// <summary>Qué hace una tarea de bodega. Se persiste por nombre; no se renombra. Los demás tipos del spec 2026-11-26 §6.2 entran con
/// su corte.</summary>
public enum WarehouseTaskType
{
    Move,
}
