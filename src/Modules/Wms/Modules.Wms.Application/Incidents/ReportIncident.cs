using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

public sealed record ReportIncidentCommand(
    Guid TenantId,
    Guid WarehouseId,
    string Type,
    Guid? LocationId,
    Guid? VariantId,
    Guid? LotId,
    decimal? Quantity,
    string? RawCode,
    string Note,
    IReadOnlyList<Guid> PhotoFileIds,
    Guid? SourceTaskId,
    DateTimeOffset? OccurredAt,
    string DeviceId) : ICommand<IncidentDto>
{
    public static ReportIncidentCommand From(Guid tenantId, ReportIncidentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un campo ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(
            tenantId,
            request.WarehouseId,
            request.Type ?? string.Empty,
            request.LocationId,
            request.VariantId,
            request.LotId,
            request.Quantity,
            request.RawCode,
            request.Note ?? string.Empty,
            request.PhotoFileIds ?? [],
            request.SourceTaskId,
            request.OccurredAt,
            request.DeviceId ?? string.Empty);
    }
}

/// <summary>Texto libre (nota y código escaneado): el dominio ya valida, pero con un código; el validador da el 422
/// <c>validation.failed</c> con el campo. Que el tipo exista es de forma; que se pueda declarar suelto lo decide el handler con
/// <c>wms.incident.type_not_allowed</c>. La evidencia no lleva regla aquí: no es texto, y el dominio y el recorder la acotan.</summary>
public sealed class ReportIncidentValidator : AbstractValidator<ReportIncidentCommand>
{
    // La columna es numeric(18,6): una cantidad mayor desbordaría en la base.
    private const decimal QuantityLimit = 1_000_000_000_000m;

    public ReportIncidentValidator()
    {
        RuleFor(command => command.Type)
            .Must(type => Enum.GetNames<IncidentType>().Contains(type, StringComparer.Ordinal))
            .WithMessage($"The incident type is one of: {string.Join(", ", Enum.GetNames<IncidentType>())}.");
        RuleFor(command => command.Note)
            .Must(note => !string.IsNullOrWhiteSpace(note) && note.Trim().Length <= Incident.NoteMaxLength)
            .WithMessage($"The note is required and has at most {Incident.NoteMaxLength} characters.");
        RuleFor(command => command.RawCode)
            .Must(code => code is null || code.Trim().Length <= Incident.RawCodeMaxLength)
            .WithMessage($"The scanned code has at most {Incident.RawCodeMaxLength} characters.");
        RuleFor(command => command.Quantity).InclusiveBetween(0m, QuantityLimit).When(command => command.Quantity is not null);
        RuleFor(command => command.OccurredAt).NotNull();
        RuleFor(command => command.DeviceId).MustBeADeviceId();
        RuleFor(command => command.LotId)
            .Null()
            .When(command => command.VariantId is null)
            .WithMessage("A lot needs its variant.");
    }
}

/// <summary>
/// El operario declara una novedad sin una línea de tarea (spec 2026-11-26 §8.5, DA-49): sólo <c>UnreadableLabel</c> y
/// <c>LocationMismatch</c>. La ruta exige <c>wms.document.read</c> y el handler, además, el permiso de algún tipo de tarea (quien
/// no puede ejecutar ninguna no tiene nada que reportar). Ruta IK. No se audita: es un hecho del operario y vive en la historia de
/// la novedad (DA-50). El Self se provisiona antes de la transacción, como en <c>ProposeAdjustmentHandler</c>.
/// </summary>
public sealed class ReportIncidentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    WmsCaller caller,
    IWmsProvisioner provisioner,
    IWmsUnitOfWork unitOfWork,
    IdempotencyScope idempotency,
    IOperationReceipts receipts,
    IClock clock,
    IOwnerRepository owners,
    IWarehouseRepository warehouses,
    ILotRepository lots,
    IWmsCatalogReader catalog,
    IWarehouseTaskRepository tasks,
    IIncidentRepository incidents,
    TaskIncidentRecorder recorder,
    IncidentDtoComposer composer,
    IValidator<ReportIncidentCommand> validator)
    : ICommandHandler<ReportIncidentCommand, IncidentDto>
{
    public async Task<IncidentDto> HandleAsync(ReportIncidentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, warehouseId: command.WarehouseId, ct: cancellationToken);
        if (TaskTypePermissions.AllowedFor(executionContext).Count == 0)
        {
            throw WmsAuthorization.Denied();
        }

        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var type = Enum.Parse<IncidentType>(command.Type);
        if (!IncidentCatalog.DeclarableStandalone.Contains(type))
        {
            throw new WmsDomainException(
                WmsErrorCodes.IncidentTypeNotAllowed, "Ese tipo de novedad no se declara sin una línea de tarea.");
        }

        var now = clock.UtcNow;
        var occurredAt = command.OccurredAt!.Value;
        LedgerEntryRules.EnsureOccurredAtInWindow(occurredAt, now);
        var member = await caller.RequireAsync(command.TenantId, cancellationToken);

        // S5: el Self lo escribe sólo el provisioner, que abre su propia transacción sobre el mismo contexto: va antes de la de aquí.
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        // La numeración toma un lock de transacción, y el await using deshace todo si guardar lanza OperationReplayedException.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var owner = (await owners.GetSelfAsync(command.TenantId, cancellationToken)).Id;
        await authorization.EnsureAsync(
            executionContext, command.TenantId, WmsPermissions.DocumentRead, owner.Value, command.WarehouseId, cancellationToken);
        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);

        LocationId? location = null;
        if (command.LocationId is { } locationId)
        {
            location = (warehouse.FindLocation(new LocationId(locationId)) ?? throw WmsNotFound.Location(locationId)).Id;
        }

        if (command.VariantId is { } variantId)
        {
            var known = await catalog.GetVariantsAsync(command.TenantId, [variantId], cancellationToken);
            if (known.Count == 0)
            {
                throw new WmsDomainException(WmsErrorCodes.ProfileVariantUnknown, $"Variant '{variantId}' is not known to the catalog.");
            }

            // La FK de la novedad al lote no mira el tenant: sólo entra un lote que el ledger aceptaría para esa variante y dueño.
            if (command.LotId is { } lotId)
            {
                await TaskLotRules.EnsureLotAsync(lots, command.TenantId, owner.Value, variantId, lotId, cancellationToken);
            }
        }

        if (command.SourceTaskId is { } sourceTaskId)
        {
            var source = await tasks.GetAsync(command.TenantId, sourceTaskId, cancellationToken);
            if (source is null || source.WarehouseId.Value != command.WarehouseId)
            {
                throw WmsNotFound.Task(sourceTaskId);
            }
        }

        var evidence = await recorder.ResolveEvidenceAsync(command.TenantId, command.PhotoFileIds, cancellationToken);
        var number = await incidents.NextNumberAsync(command.TenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        var subject = new IncidentSubject(
            command.SourceTaskId,
            null,
            location,
            command.VariantId,
            command.LotId is { } lot ? new LotId(lot) : null,
            null,
            null,
            command.Quantity);
        var incident = Incident.Report(
            command.TenantId,
            number,
            warehouse.Id,
            owner,
            type,
            subject,
            command.Note,
            command.RawCode,
            null,
            evidence,
            member,
            command.DeviceId,
            idempotency.Key,
            occurredAt,
            now);
        incidents.Add(incident);

        await unitOfWork.FlushAsync(cancellationToken);
        var dto = await composer.ComposeAsync(incident, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }
}
