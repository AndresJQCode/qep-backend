using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Domain.Tasks;

/// <summary>Lo que cambia de un tipo de tarea a otro (spec 2026-11-26 §6.2). Un tipo nuevo entra en cada uno de estos métodos.</summary>
public static class TaskTypePolicy
{
    /// <summary>Cuánto tiempo «Siguiente tarea» no vuelve a ofrecerle a una persona la que saltó (DA-33).</summary>
    public static readonly TimeSpan SkipCooldown = TimeSpan.FromMinutes(15);

    public static int DefaultPriority(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move or WarehouseTaskType.Count => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Cuánto dura un reclamo sin latido ni confirmación. Un conteo dura más: el operario cuenta, y recuenta, en la
    /// ubicación.</summary>
    public static TimeSpan LeaseDuration(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => TimeSpan.FromMinutes(15),
        WarehouseTaskType.Count => TimeSpan.FromMinutes(30),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Si se puede confirmar en un destino distinto del planeado, con motivo.</summary>
    public static bool AllowsDestinationOverride(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => true,
        WarehouseTaskType.Count => false,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Por qué razón mueve stock una tarea de ese tipo. Un conteo no mueve nada: el movimiento lo hace el ajuste que él
    /// propone, ya aprobado (por eso lanza).</summary>
    public static MovementReason MovementReasonOf(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => MovementReason.InternalMove,
        WarehouseTaskType.Count => throw new ArgumentOutOfRangeException(nameof(type), "A count never moves stock."),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Si la línea se cierra contando (<c>count</c>) en vez de confirmando un movimiento (<c>confirm</c>, <c>short</c>).
    /// Un tipo nuevo decide aquí con cuál de las dos acciones cierra sus líneas.</summary>
    public static bool ConfirmsByCount(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => false,
        WarehouseTaskType.Count => true,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
