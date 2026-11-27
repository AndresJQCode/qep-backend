using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

/// <summary>Una tarea pasó a Done o Cancelled en esta transacción. ActorMemberId es la membresía que la cerró; OperationKey, la
/// clave de la acción del operario (null en las del supervisor).</summary>
public sealed record TaskClosed(
    WarehouseTask Task, Guid ActorMemberId, string? OperationKey, DateTimeOffset OccurredAt, DateTimeOffset Now);

/// <summary>Lo que otro proceso hace cuando una tarea se cierra (T2: el Count de verificación). Corre dentro de la transacción del
/// cierre, antes de guardar: si falla, no se guarda nada.</summary>
public interface ITaskClosedHook
{
    Task OnClosedAsync(TaskClosed closed, CancellationToken cancellationToken);
}

/// <summary>El único punto por el que pasa el cierre de una tarea. Lo llaman los dos caminos que pueden cerrarla: OperatorLineRunner
/// (confirmar, faltante, conteo) y SupervisorTaskRunner (cancelar la tarea o su última línea abierta).</summary>
public sealed class TaskClosedNotifier(IEnumerable<ITaskClosedHook> hooks)
{
    /// <summary>No hace nada salvo que la tarea estuviera abierta (<paramref name="before"/>) y ahora esté Done o Cancelled;
    /// entonces corre cada gancho, en el orden de registro.</summary>
    public async Task NotifyIfClosedAsync(WarehouseTaskStatus before, TaskClosed closed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(closed);
        if (IsTerminal(before) || !IsTerminal(closed.Task.Status))
        {
            return;
        }

        foreach (var hook in hooks)
        {
            await hook.OnClosedAsync(closed, cancellationToken);
        }
    }

    private static bool IsTerminal(WarehouseTaskStatus status) =>
        status is WarehouseTaskStatus.Done or WarehouseTaskStatus.Cancelled;
}
