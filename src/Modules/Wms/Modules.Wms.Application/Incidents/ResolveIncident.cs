using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

public sealed record ResolveIncidentCommand(Guid TenantId, Guid IncidentId, long Version, string Resolution, string? Note)
    : ICommand<IncidentResolutionDto>
{
    public static ResolveIncidentCommand From(Guid tenantId, Guid incidentId, ResolveIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Una resolución ausente llega null: se vuelve vacía para que la atrape el validador.
        return new(tenantId, incidentId, request.Version, request.Resolution ?? string.Empty, request.Note);
    }
}

/// <summary>La resolución es un nombre del enum y la nota es texto libre: el dominio ya valida, pero con un código; el validador da
/// el campo. Qué resolución admite cada tipo lo decide el dominio con el catálogo.</summary>
public sealed class ResolveIncidentValidator : AbstractValidator<ResolveIncidentCommand>
{
    public ResolveIncidentValidator()
    {
        RuleFor(command => command.Resolution)
            .Must(resolution => Enum.GetNames<IncidentResolution>().Contains(resolution, StringComparer.Ordinal))
            .WithMessage($"The resolution must be one of: {string.Join(", ", Enum.GetNames<IncidentResolution>())}.");
        RuleFor(command => command.Note)
            .Must(note => note is null || note.Trim().Length <= Incident.NoteMaxLength)
            .WithMessage($"The note has at most {Incident.NoteMaxLength} characters.");
    }
}

/// <summary>
/// Resuelve una novedad (spec 2026-11-26 §8.4 y §8.5). Ruta SK (S2): la clave es <c>incident:{id}:resolve:{version}</c>. <c>Accept</c>
/// no mueve nada. <c>AcceptIntoStock</c> de un <c>SyncConflict</c> repite el movimiento de la línea por lo encontrado, con el
/// supervisor como actor (DA-48): la tarea no se toca (por eso no se bloquea), y el movimiento y la resolución se confirman en la
/// misma transacción. Quien reportó no acepta en el stock su propia novedad (DA-17, DA-47; se comparan membresías). Una sola llamada
/// al ledger por transacción.
/// </summary>
public sealed class ResolveIncidentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope scope,
    IOperationReceipts receipts,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IIncidentRepository incidents,
    IWarehouseTaskRepository tasks,
    IInventoryLedger ledger,
    IValidator<ResolveIncidentCommand> validator)
    : ICommandHandler<ResolveIncidentCommand, IncidentResolutionDto>
{
    public async Task<IncidentResolutionDto> HandleAsync(ResolveIncidentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.DiscrepancyResolve, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var key = scope.Key ?? throw new InvalidOperationException("Resolving an incident must run behind the server idempotency key.");
        var resolution = Enum.Parse<IncidentResolution>(command.Resolution);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (!await unitOfWork.TryLockDocumentAsync(command.IncidentId, cancellationToken))
        {
            throw new ResourceConflictException(
                WmsErrorCodes.DocumentBusy, "Esta novedad se está procesando en este momento. Intenta de nuevo en unos segundos.");
        }

        var incident = await incidents.GetAsync(command.TenantId, command.IncidentId, cancellationToken)
            ?? throw WmsNotFound.Incident(command.IncidentId);
        await authorization.EnsureAsync(
            executionContext,
            command.TenantId,
            WmsPermissions.DiscrepancyResolve,
            incident.OwnerId.Value,
            incident.WarehouseId.Value,
            cancellationToken);
        WmsVersions.Ensure(incident.Version, command.Version);

        var member = await caller.RequireAsync(command.TenantId, cancellationToken);
        incident.EnsureNotSelfResolution(resolution, member);
        // Antes del ledger: una novedad que no admite la resolución no debe llegar a mover stock, y deshacerlo sería trabajo tirado.
        incident.EnsureCanResolve(resolution);

        var now = clock.UtcNow;
        Guid? batchId = null;
        if (resolution == IncidentResolution.AcceptIntoStock)
        {
            batchId = await MoveIntoStockAsync(incident, member, key, now, cancellationToken);
        }

        incident.Resolve(resolution, member, command.Note, batchId, now);
        var changed = new Dictionary<string, string?>
        {
            ["ownerId"] = incident.OwnerId.Value.ToString(),
            ["number"] = incident.Number,
            ["type"] = incident.Type.ToString(),
            ["resolution"] = resolution.ToString(),
        };
        if (batchId is { } batch)
        {
            changed["batchId"] = batch.ToString();
        }

        if (incident.ResolutionNote is { } note)
        {
            changed["note"] = note;
        }

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            IncidentResolved,
            Resources.Incident,
            incident.Id.ToString(),
            changed,
            now);

        var dto = new IncidentResolutionDto(batchId, []);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }

    // La acción rechazada por el lease perdido, aplicada como movimiento del supervisor: (dueño, ubicación, variante, lote, estado) →
    // (dueño, destino de la línea, ...) por lo encontrado. El destino se lee sin cargar la tarea: nadie la bloquea ni la modifica.
    private async Task<Guid> MoveIntoStockAsync(
        Incident incident, Guid member, string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lineId = incident.SourceTaskLineId;
        // TryGetValue y no GetValueOrDefault: una línea sin destino (la de un Count) daría Guid.Empty, que no es null.
        Guid? destination = null;
        if (lineId is { } id)
        {
            var destinations = await tasks.FindLineDestinationsAsync(incident.TenantId, [id], cancellationToken);
            destination = destinations.TryGetValue(id, out var found) ? found : null;
        }

        if (!IncidentResolutionRules.CanAcceptIntoStock(incident, destination))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentResolutionNotAllowed, "Esta novedad no tiene un movimiento que aplicar: sólo se rechaza.");
        }

        var owner = incident.OwnerId.Value;
        var lot = incident.LotId?.Value;
        var status = incident.StockStatus!.Value;
        var variant = incident.VariantId!.Value;
        // La hora del escaneo si el ledger todavía la admite (ventana de 30 días); si no, la de la decisión del supervisor.
        var occurredAt = incident.ReportedAt >= now.AddDays(-LedgerEntryRules.MaxPastDays) ? incident.ReportedAt : now;
        var entry = new LedgerEntry(
            new StockKey(owner, incident.LocationId!.Value.Value, variant, lot, status),
            new StockKey(owner, destination!.Value, variant, lot, status),
            incident.ActualQty!.Value,
            null,
            MovementReason.InternalMove,
            new LedgerDocument("Incident", incident.Id, lineId),
            occurredAt);
        var posting = await ledger.PostAsync(
            new LedgerBatch(
                incident.TenantId, Guid.CreateVersion7(), key, new LedgerActor(member, LedgerActorType.Human, null), [entry]),
            cancellationToken);
        return posting.BatchId;
    }
}
