using BuildingBlocks.Application;
using FluentValidation;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record CountTaskLineCommand(
    Guid TenantId,
    Guid TaskId,
    Guid LineId,
    Guid LeaseToken,
    Guid LocationId,
    Guid? LotId,
    decimal QuantityCounted,
    DateTimeOffset? OccurredAt,
    string DeviceId) : ICommand<TaskDto>
{
    public static CountTaskLineCommand From(Guid tenantId, Guid taskId, Guid lineId, CountTaskLineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new(
            tenantId,
            taskId,
            lineId,
            request.LeaseToken,
            request.LocationId,
            request.LotId,
            request.QuantityCounted,
            request.OccurredAt,
            request.DeviceId ?? string.Empty);
    }
}

/// <summary>La ventana y el piso de <c>OccurredAt</c> no se miran aquí: los aplican la tarea y el ledger, con el código del ledger.</summary>
public sealed class CountTaskLineValidator : AbstractValidator<CountTaskLineCommand>
{
    // La columna es numeric(18,6): una cantidad mayor desbordaría en la base.
    private const decimal QuantityLimit = 1_000_000_000_000m;

    public CountTaskLineValidator()
    {
        RuleFor(command => command.DeviceId).MustBeADeviceId();
        RuleFor(command => command.QuantityCounted).InclusiveBetween(0m, QuantityLimit);
        RuleFor(command => command.OccurredAt).NotNull();
    }
}

/// <summary>
/// El operario cuenta lo que hay en la ubicación de la línea de un <c>Count</c> (spec 2026-11-26 §6.2 y §8.4, DA-40). Es a ciegas: el
/// servidor nunca le dice lo esperado. Cerrar la línea cierra la tarea, y <see cref="CountVerificationHook"/> (dentro de esta misma
/// transacción) compara lo contado con el saldo y resuelve las novedades o propone el ajuste. No escribe al ledger y <b>no toma el
/// <c>count-key</c></b>: ya tiene la fila del <c>Count</c>, y tomarlo después invertiría el orden de bloqueos de <c>short</c>. Ruta IK.
/// </summary>
public sealed class CountTaskLineHandler(
    OperatorLineRunner runner,
    IVariantProfileRepository profiles,
    ILotRepository lots,
    IIncidentRepository incidents,
    IValidator<CountTaskLineCommand> validator)
    : ICommandHandler<CountTaskLineCommand, TaskDto>
{
    public async Task<TaskDto> HandleAsync(CountTaskLineCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await runner.AuthorizeAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var occurredAt = command.OccurredAt!.Value;

        return await runner.RunAsync(
            new OperatorLineRequest(
                command.TenantId,
                command.TaskId,
                command.LineId,
                command.LeaseToken,
                command.DeviceId,
                occurredAt,
                "count",
                command,
                command.QuantityCounted),
            (context, ct) => ApplyAsync(command, occurredAt, context, ct),
            cancellationToken);
    }

    private async Task ApplyAsync(
        CountTaskLineCommand command, DateTimeOffset occurredAt, OperatorLineContext context, CancellationToken cancellationToken)
    {
        var (task, line, member) = (context.Task, context.Line, context.MemberId);

        // Antes que cualquier otra comprobación: contar una línea de movimiento es una transición inválida, no un error de lote.
        if (!TaskTypePolicy.ConfirmsByCount(task.Type))
        {
            throw new TaskTransitionException(task.Status.ToString(), "count-line");
        }

        // Decisión 33, defensa en profundidad: aunque el Count llegara a sus manos (lo tenía en curso antes de reportar), quien reportó
        // la novedad que verifica no lo cuenta.
        await CountSelfVerificationRule.EnsureNotReporterAsync(incidents, command.TenantId, task, member, cancellationToken);

        if (command.LocationId != line.FromLocationId.Value)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLocationMismatch, "La ubicación escaneada no es la de la línea.");
        }

        var profile = await profiles.FindAsync(command.TenantId, line.VariantId, cancellationToken);

        // Un Count de una variante de series no se genera (DA-44); esto atrapa un cambio de control posterior a la generación:
        // no se registra nada y el supervisor cancela la tarea.
        if (profile?.Tracking == TrackingPolicy.Serial)
        {
            throw new WmsDomainException(
                WmsErrorCodes.SerialRequired, "Esta variante se controla por series: no se cuenta por cantidad, el supervisor la resuelve.");
        }

        if (line.LotId is { } fixedLot && command.LotId is { } scannedFixed && scannedFixed != fixedLot.Value)
        {
            throw new WmsDomainException(WmsErrorCodes.TaskLotMismatch, "El lote escaneado no es el de la línea.");
        }

        var lot = line.LotId?.Value ?? command.LotId;

        // Sin lote no hay saldo que comparar, y se propondría un ajuste que el ledger rechazaría al contabilizar.
        if (lot is null && profile?.Tracking == TrackingPolicy.Lot)
        {
            throw new WmsDomainException(WmsErrorCodes.LotRequired, "Esta variante se controla por lote: escanea el lote que contaste.");
        }

        if (line.LotId is null && command.LotId is { } scanned)
        {
            await TaskLotRules.EnsureLotAsync(
                lots, command.TenantId, task.OwnerId.Value, line.VariantId, scanned, cancellationToken);
        }

        task.RecordCount(
            member,
            command.LeaseToken,
            command.DeviceId,
            line.Id,
            command.QuantityCounted,
            lot is { } lotId ? new LotId(lotId) : null,
            context.Now,
            occurredAt,
            context.MemberIsBusy);
    }
}
