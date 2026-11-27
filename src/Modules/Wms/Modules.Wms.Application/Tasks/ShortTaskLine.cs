using BuildingBlocks.Application;
using FluentValidation;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record ShortTaskLineCommand(
    Guid TenantId,
    Guid TaskId,
    Guid LineId,
    Guid LeaseToken,
    decimal QuantityFound,
    Guid? LotId,
    string IncidentType,
    string? Note,
    DateTimeOffset? OccurredAt,
    string DeviceId,
    IReadOnlyList<Guid>? PhotoFileIds) : ICommand<TaskDto>
{
    public static ShortTaskLineCommand From(Guid tenantId, Guid taskId, Guid lineId, ShortTaskLineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un tipo ausente llega null: se vuelve vacío para que lo atrape el validador.
        return new(
            tenantId,
            taskId,
            lineId,
            request.LeaseToken,
            request.QuantityFound,
            request.LotId,
            request.IncidentType ?? string.Empty,
            request.Note,
            request.OccurredAt,
            request.DeviceId ?? string.Empty,
            request.PhotoFileIds);
    }
}

/// <summary>Que la cantidad sea menor que lo que falta, la ventana y el piso de <c>OccurredAt</c> y qué tipos admite la tarea los
/// decide el dominio, que ve la línea y la tarea.</summary>
public sealed class ShortTaskLineValidator : AbstractValidator<ShortTaskLineCommand>
{
    // La columna es numeric(18,6): una cantidad mayor desbordaría en la base.
    private const decimal QuantityLimit = 1_000_000_000_000m;

    public ShortTaskLineValidator()
    {
        RuleFor(command => command.DeviceId).MustBeADeviceId();
        RuleFor(command => command.QuantityFound).InclusiveBetween(0m, QuantityLimit);
        RuleFor(command => command.IncidentType)
            .Must(type => Enum.GetNames<IncidentType>().Contains(type, StringComparer.Ordinal))
            .WithMessage($"The incident type is one of: {string.Join(", ", Enum.GetNames<IncidentType>())}.");
        RuleFor(command => command.Note)
            .Must(note => note is null || note.Trim().Length <= Incident.NoteMaxLength)
            .WithMessage($"The note has at most {Incident.NoteMaxLength} characters.");
        RuleFor(command => command.OccurredAt).NotNull();
    }
}

/// <summary>
/// El operario no encontró todo lo que faltaba de una línea (spec 2026-11-26 §6.8 y §8.4). Confirma lo encontrado hacia el destino
/// planeado, cierra la línea como <c>Short</c> y deja la novedad en la misma transacción y con la misma <c>Idempotency-Key</c>. Ruta
/// IK. Nunca ajusta stock (DA-18): la diferencia la propone el conteo. Mismo orden de bloqueos que <c>confirm</c>, con la numeración
/// de novedades antes del ledger.
/// </summary>
public sealed class ShortTaskLineHandler(
    OperatorLineRunner runner,
    TaskIncidentRecorder recorder,
    IInventoryLedger ledger,
    ILotRepository lots,
    IValidator<ShortTaskLineCommand> validator)
    : ICommandHandler<ShortTaskLineCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(ShortTaskLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var occurredAt = command.OccurredAt!.Value;
        var type = Enum.Parse<IncidentType>(command.IncidentType);

        return await runner.RunAsync(
            new OperatorLineRequest(
                command.TenantId,
                command.TaskId,
                command.LineId,
                command.LeaseToken,
                command.DeviceId,
                occurredAt,
                "short",
                command,
                command.QuantityFound),
            (context, ct) => ApplyAsync(command, type, occurredAt, context, ct),
            cancellationToken);
    }

    private async Task ApplyAsync(
        ShortTaskLineCommand command,
        IncidentType type,
        DateTimeOffset occurredAt,
        OperatorLineContext context,
        CancellationToken cancellationToken)
    {
        var (task, line, member) = (context.Task, context.Line, context.MemberId);

        // Una línea de conteo no declara faltante: se cuenta. Antes que el tipo de novedad, que tampoco admite un Count.
        if (TaskTypePolicy.ConfirmsByCount(task.Type))
        {
            throw new TaskTransitionException(task.Status.ToString(), "short-line");
        }

        if (!IncidentCatalog.DeclarableOnShort(task.Type.ToString()).Contains(type))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentTypeNotAllowed, "Ese tipo de novedad no se declara en una tarea de este tipo.");
        }

        if (line.LotId is { } planned && command.LotId != planned.Value)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLotMismatch, "El lote escaneado no es el de la línea.");
        }

        // Con cero encontrado no hay llamada al ledger que atrape un lote ajeno, y la FK de la novedad al lote no mira el tenant.
        if (line.LotId is null && command.LotId is { } scanned)
        {
            await TaskLotRules.EnsureLotAsync(
                lots, command.TenantId, task.OwnerId.Value, line.VariantId, scanned, cancellationToken);
        }

        var lot = line.LotId?.Value ?? command.LotId;
        var expected = line.Remaining;
        task.ShortLine(
            member, command.LeaseToken, command.DeviceId, line.Id, command.QuantityFound, context.Now, occurredAt, context.MemberIsBusy);

        // La numeración toma un lock: antes que los saldos que toca el ledger.
        await recorder.ReportFromLineAsync(
            task,
            line,
            type,
            expected,
            command.QuantityFound,
            lot is { } lotId ? new LotId(lotId) : null,
            command.Note,
            command.PhotoFileIds,
            member,
            command.DeviceId,
            context.OperationKey,
            occurredAt,
            context.Now,
            cancellationToken);

        if (command.QuantityFound <= 0)
        {
            return;
        }

        var owner = task.OwnerId.Value;
        var to = line.ToLocationId?.Value
            ?? throw new InvalidOperationException($"Line {line.Id} of task {task.Id} has no destination.");
        var entry = new LedgerEntry(
            new StockKey(owner, line.FromLocationId.Value, line.VariantId, lot, line.StockStatus),
            new StockKey(owner, to, line.VariantId, lot, line.StockStatus),
            command.QuantityFound,
            null,
            TaskTypePolicy.MovementReasonOf(task.Type),
            new LedgerDocument("Task", task.Id, line.Id),
            occurredAt);
        await ledger.PostAsync(
            new LedgerBatch(
                command.TenantId,
                Guid.CreateVersion7(),
                context.OperationKey,
                new LedgerActor(member, LedgerActorType.Human, command.DeviceId.Trim()),
                [entry]),
            cancellationToken);
    }
}
