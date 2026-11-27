using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

/// <summary>Spec 2026-11-26 §6.2: ejecutar una tarea exige el permiso de su proceso.</summary>
public static class TaskTypePermissions
{
    // Un movimiento interno usa el del operario que ya mueve mercancía entre bins; un conteo, el de contar. Un tipo nuevo entra aquí.
    public static string For(WarehouseTaskType type) => type switch
    {
        WarehouseTaskType.Move => WmsPermissions.PutawayPerform,
        WarehouseTaskType.Count => WmsPermissions.CountPerform,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Los tipos de tarea que quien llama puede ejecutar.</summary>
    public static IReadOnlyList<WarehouseTaskType> AllowedFor(IExecutionContext executionContext)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        return [.. Enum.GetValues<WarehouseTaskType>().Where(type => executionContext.HasPermission(For(type)))];
    }
}
