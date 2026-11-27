using System.Diagnostics;
using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record OperatorLineRequest(
    Guid TenantId,
    Guid TaskId,
    Guid LineId,
    Guid LeaseToken,
    string DeviceId,
    DateTimeOffset OccurredAt,
    string Action,
    object RawCommand,
    decimal? Quantity);

public sealed record OperatorLineContext(
    WarehouseTask Task,
    WarehouseTaskLine Line,
    Guid MemberId,
    LeaseCheck Lease,
    bool MemberIsBusy,
    string OperationKey,
    DateTimeOffset Now);

/// <summary>
/// El molde de toda acción del operario sobre una línea (confirmar, faltante, conteo): clave IK → membresía → transacción → lock del
/// operario → lock de la tarea (404) → carga → permiso del tipo → lease → línea (404) → DA-32 → la acción → cierre → componer →
/// recibo → guardar. Una sola llamada al ledger por transacción, dentro de la acción.
/// </summary>
public sealed class OperatorLineRunner(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope,
    IOperationReceipts receipts,
    IWarehouseTaskRepository tasks,
    OperatorTaskGuard guard,
    IWmsConfirmMetrics metrics,
    IClock clock,
    WarehouseTaskDtoComposer composer,
    TaskClosedNotifier closed,
    TaskIncidentRecorder recorder)
{
    /// <summary>La ruta exige <c>wms.document.read</c>; se comprueba antes del validador.</summary>
    public Task AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken) =>
        authorization.EnsureAsync(executionContext, tenantId, WmsPermissions.DocumentRead, ct: cancellationToken);

    /// <param name="apply">La acción propia: muta la tarea y, si mueve stock, hace la única llamada al ledger.</param>
    public async Task<TaskDto> RunAsync(
        OperatorLineRequest request,
        Func<OperatorLineContext, CancellationToken, Task> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(apply);
        var key = scope.Key ?? throw new InvalidOperationException("A task line action must run behind an idempotency key.");
        var member = await caller.RequireAsync(request.TenantId, cancellationToken);

        var started = Stopwatch.GetTimestamp();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await unitOfWork.LockAsync(TaskMemberLocks.For(member), cancellationToken);
        if (!await tasks.LockAsync(request.TenantId, request.TaskId, cancellationToken))
        {
            throw WmsNotFound.Task(request.TaskId);
        }

        var task = await tasks.GetAsync(request.TenantId, request.TaskId, cancellationToken)
            ?? throw WmsNotFound.Task(request.TaskId);
        await guard.EnsureCanPerformAsync(request.TenantId, task, cancellationToken);
        var now = clock.UtcNow;

        // Spec §6.7 y DA-48: la acción rechazada queda como novedad aunque el hecho no se aplique. La transacción sólo tiene la
        // novedad (nada más se tocó), así que guardarla y lanzar después no deja nada a medias. El filtro IK no encuentra recibo y
        // devuelve este 409 tal cual; un reintento con la misma clave no duplica la novedad (operation_key, bajo el lock del operario).
        var lease = task.CheckLease(member, request.LeaseToken);
        if (lease == LeaseCheck.Lost)
        {
            await recorder.RecordSyncConflictAsync(
                task,
                task.FindLine(request.LineId),
                member,
                request.DeviceId,
                key,
                request.Action,
                request.RawCommand,
                request.Quantity,
                request.OccurredAt,
                now,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new TaskLeaseLostException(resumable: false);
        }

        // El dominio trata una línea ajena como error de cableado: el 404 sale de acá.
        var line = task.FindLine(request.LineId) ?? throw WmsNotFound.TaskLine(request.LineId);
        // DA-32: bajo el lock de la persona, si mientras tanto tomó otra tarea ésta no vuelve a InProgress. Lo que movió en el piso se
        // anota igual.
        var busy = lease == LeaseCheck.Resumable
            && await tasks.FindInProgressOfMemberAsync(request.TenantId, member, cancellationToken) is not null;
        var before = task.Status;

        await apply(new OperatorLineContext(task, line, member, lease, busy, key, now), cancellationToken);
        await closed.NotifyIfClosedAsync(
            before, new TaskClosed(task, member, key, request.OccurredAt, now), cancellationToken);

        var dto = await composer.ComposeAsync(task, member, withEvents: false, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        metrics.RecordConfirmDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return dto;
    }
}
