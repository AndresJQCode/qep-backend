using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Domain.Tasks;

/// <summary>Lo que cambia de un tipo de tarea a otro (spec 2026-11-26 §6.2). Un tipo nuevo entra en cada uno de estos métodos.</summary>
public static class TaskTypePolicy
{
    /// <summary>Cuánto tiempo «Siguiente tarea» no vuelve a ofrecerle a una persona la que saltó (DA-33).</summary>
    public static readonly TimeSpan SkipCooldown = TimeSpan.FromMinutes(15);

    public static int DefaultPriority(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Cuánto dura un reclamo sin latido ni confirmación.</summary>
    public static TimeSpan LeaseDuration(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => TimeSpan.FromMinutes(15),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Si se puede confirmar en un destino distinto del planeado, con motivo.</summary>
    public static bool AllowsDestinationOverride(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => true,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static MovementReason MovementReasonOf(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => MovementReason.InternalMove,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
