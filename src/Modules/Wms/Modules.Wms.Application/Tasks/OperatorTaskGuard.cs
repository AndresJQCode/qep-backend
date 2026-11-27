using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

/// <summary>Lo que toda acción del operario sobre una tarea comprueba: que pueda hacer ese tipo de tarea y que su token siga siendo
/// el del reclamo (spec 2026-11-26 §6.7).</summary>
public sealed class OperatorTaskGuard(IExecutionContext executionContext, WmsAuthorization authorization)
{
    /// <summary>La ruta exige <c>wms.document.read</c>; el permiso del proceso depende del tipo de la tarea y se exige aquí.
    /// </summary>
    public async Task EnsureCanPerformAsync(Guid tenantId, WarehouseTask task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await authorization.EnsureAsync(
            executionContext,
            tenantId,
            WmsPermissions.DocumentRead,
            task.OwnerId.Value,
            task.WarehouseId.Value,
            cancellationToken);
        if (!executionContext.HasPermission(TaskTypePermissions.For(task.Type)))
        {
            throw WmsAuthorization.Denied();
        }
    }

    /// <summary>Sólo el reclamo vigente: latir y soltar no reanudan nada (sólo una confirmación trae un hecho físico que no se puede
    /// descartar). Si no, 409 con <c>resumable</c>.</summary>
    public static void EnsureActiveLease(WarehouseTask task, Guid memberId, Guid leaseToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var lease = task.CheckLease(memberId, leaseToken);
        if (lease != LeaseCheck.Valid)
        {
            throw new TaskLeaseLostException(resumable: lease == LeaseCheck.Resumable);
        }
    }
}

/// <summary>Decisión del owner, 2026-11-27 (decisión 33): nadie cuenta el <c>Count</c> que verifica una novedad que reportó. Ni lo
/// recibe con <c>next</c>, ni lo reclama por id, ni el supervisor se lo asigna, ni lo cuenta con la acción <c>count</c> (defensa en
/// profundidad). En una bodega de una persona el conteo espera y el supervisor resuelve la novedad a mano.</summary>
internal static class CountSelfVerificationRule
{
    public static async Task EnsureNotReporterAsync(
        IIncidentRepository incidents, Guid tenantId, WarehouseTask task, Guid memberId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(task);
        if (task.Type == WarehouseTaskType.Count
            && await incidents.HasReportedVerifiedByAsync(tenantId, task.Id, memberId, cancellationToken))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentSelfResolution,
                "Quien reportó la novedad no cuenta el conteo que la verifica: lo cuenta otra persona.");
        }
    }
}

internal static class TaskMemberLocks
{
    /// <summary>La clave del lock bloqueante por operario: serializa dos peticiones de la misma persona. Va siempre antes que el
    /// bloqueo de la tarea, y éste antes que los saldos.</summary>
    public static string For(Guid memberId) => $"task-member:{memberId:D}";
}

internal static class TaskDeviceRules
{
    public static IRuleBuilderOptions<T, string> MustBeADeviceId<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(device => !string.IsNullOrWhiteSpace(device) && device.Trim().Length <= WarehouseTask.DeviceIdMaxLength)
            .WithMessage($"The device id is required and has at most {WarehouseTask.DeviceIdMaxLength} characters.");
}
