using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25). <c>OwnerId</c> nulo es «el Self del tenant».</summary>
public sealed record ProposeAdjustmentCommand(
    Guid TenantId,
    Guid WarehouseId,
    Guid? OwnerId,
    string Reason,
    string? Note,
    DateTimeOffset? OccurredAt,
    IReadOnlyList<AdjustmentLineRequest> Lines,
    bool AutoApprove) : ICommand<AdjustmentDto>
{
    public static ProposeAdjustmentCommand From(Guid tenantId, ProposeAdjustmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un campo ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(
            tenantId,
            request.WarehouseId,
            request.OwnerId,
            request.Reason ?? string.Empty,
            request.Note,
            request.OccurredAt,
            request.Lines ?? [],
            request.AutoApprove);
    }
}

internal static class AdjustmentEnums
{
    public static bool IsReason(string? value) => value is not null && Enum.GetNames<AdjustmentReason>().Contains(value, StringComparer.Ordinal);

    public static bool IsStatus(string? value) => value is not null && Enum.GetNames<StockStatus>().Contains(value, StringComparer.Ordinal);
}

/// <summary>El dominio ya valida las líneas, pero da un código y no el campo: el validador da el 422 <c>validation.failed</c> con el
/// mapa <c>errors</c> que el formulario sabe leer (<c>Lines[0].Status</c>).</summary>
public sealed class ProposeAdjustmentValidator : AbstractValidator<ProposeAdjustmentCommand>
{
    public ProposeAdjustmentValidator()
    {
        RuleFor(command => command.Reason)
            .Must(AdjustmentEnums.IsReason)
            .WithMessage($"The reason must be one of: {string.Join(", ", Enum.GetNames<AdjustmentReason>())}.");
        RuleFor(command => command.Note).MaximumLength(Adjustment.NoteMaxLength);
        RuleFor(command => command.Lines)
            .Must(lines => lines.Count is >= 1 and <= Adjustment.MaxLines)
            .WithMessage($"An adjustment has between 1 and {Adjustment.MaxLines} lines.");
        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.Status)
                .Must(AdjustmentEnums.IsStatus)
                .WithMessage($"The status must be one of: {string.Join(", ", Enum.GetNames<StockStatus>())}.");
            line.RuleFor(item => item.QuantityDelta).NotEqual(0m).WithMessage("The quantity cannot be zero.");
            line.RuleFor(item => item.LotCode).MaximumLength(Lot.CodeMaxLength);
            line.RuleFor(item => item.CapturedUom).MaximumLength(AdjustmentLine.CapturedUomMaxLength);
        });
    }
}

public sealed class ProposeAdjustmentHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IWmsUnitOfWork unitOfWork,
    IOperationReceipts receipts,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IOwnerRepository owners,
    IWarehouseRepository warehouses,
    ILotRepository lots,
    IVariantProfileRepository profiles,
    IAdjustmentRepository adjustments,
    IWmsCatalogReader catalog,
    AdjustmentDtoComposer composer,
    IValidator<ProposeAdjustmentCommand> validator)
    : ICommandHandler<ProposeAdjustmentCommand, AdjustmentDto>
{
    public async Task<AdjustmentDto> HandleAsync(ProposeAdjustmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Hoy la ruta exige proponer (§24.3), así que la rama de «sólo aprueba» no se alcanza por HTTP: queda para cuando otro
        // camino (un worker, una API externa) llame al caso de uso sin pasar por la política de la ruta.
        var permission = executionContext.HasPermission(WmsPermissions.AdjustmentPropose)
            ? WmsPermissions.AdjustmentPropose
            : WmsPermissions.AdjustmentApprove;
        await authorization.EnsureAsync(executionContext, command.TenantId, permission, warehouseId: command.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // S5: sin dueño el ajuste es del Self, y el Self lo escribe sólo el provisioner. Va antes de abrir la transacción: abre la suya
        // sobre el mismo contexto y EF no admite una transacción dentro de otra.
        if (command.OwnerId is null)
        {
            await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        }

        // S26: la numeración toma un lock de transacción, y el await using deshace todo si SaveChangesAsync lanza
        // OperationReplayedException (la perdedora de dos peticiones con la misma clave).
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var now = clock.UtcNow;
        var ownerId = await ResolveOwnerAsync(command, cancellationToken);
        await authorization.EnsureAsync(
            executionContext, command.TenantId, permission, ownerId, command.WarehouseId, cancellationToken);
        var warehouse = await warehouses.GetAsync(command.TenantId, new WarehouseId(command.WarehouseId), cancellationToken)
            ?? throw WmsNotFound.Warehouse(command.WarehouseId);

        await EnsureVariantsExistAsync(command, cancellationToken);
        var lines = await BuildLinesAsync(command, warehouse, ownerId, now, cancellationToken);

        var number = await adjustments.NextNumberAsync(command.TenantId, DateOnly.FromDateTime(now.UtcDateTime), cancellationToken);
        var adjustment = Adjustment.Propose(
            command.TenantId,
            number,
            new OwnerId(ownerId),
            warehouse.Id,
            Enum.Parse<AdjustmentReason>(command.Reason),
            command.Note,
            command.OccurredAt,
            lines,
            executionContext.SubjectId,
            now);
        adjustments.Add(adjustment);

        var changed = new Dictionary<string, string?>
        {
            ["ownerId"] = ownerId.ToString(),
            ["number"] = adjustment.Number,
            ["reason"] = adjustment.Reason.ToString(),
            ["lineCount"] = adjustment.Lines.Count.ToString(CultureInfo.InvariantCulture),
        };
        if (adjustment.Note is not null)
        {
            changed["note"] = adjustment.Note;
        }

        auditRecorder.Record(
            command.TenantId, executionContext.SubjectId, WmsActorType.Human, AdjustmentProposed, Resources.Adjustment, adjustment.Id.ToString(), changed, now);

        if (command.AutoApprove && executionContext.HasPermission(WmsPermissions.AdjustmentApprove))
        {
            adjustment.Approve(executionContext.SubjectId, now);
            auditRecorder.Record(
                command.TenantId,
                executionContext.SubjectId,
                WmsActorType.Human,
                AdjustmentApproved,
                Resources.Adjustment,
                adjustment.Id.ToString(),
                new Dictionary<string, string?> { ["ownerId"] = ownerId.ToString(), ["number"] = adjustment.Number },
                now);
        }

        var dto = await composer.ComposeAsync(adjustment, warehouse, cancellationToken);
        receipts.Record(dto);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return dto;
    }

    private async Task<Guid> ResolveOwnerAsync(ProposeAdjustmentCommand command, CancellationToken cancellationToken)
    {
        if (command.OwnerId is not { } requested)
        {
            return (await owners.GetSelfAsync(command.TenantId, cancellationToken)).Id.Value;
        }

        var owner = await owners.FindAsync(command.TenantId, new OwnerId(requested), cancellationToken)
            ?? throw WmsNotFound.Owner(requested);
        return owner.Id.Value;
    }

    // El perfil por defecto de una variante lo crea el ledger al contabilizar (y falla ahí si Catalog no la conoce): aquí se frena
    // antes, con el mismo código, para no guardar una propuesta que no se podrá contabilizar nunca.
    private async Task EnsureVariantsExistAsync(ProposeAdjustmentCommand command, CancellationToken cancellationToken)
    {
        var variantIds = command.Lines.Select(line => line.VariantId).Distinct().ToList();
        var known = (await catalog.GetVariantsAsync(command.TenantId, variantIds, cancellationToken)).Select(item => item.VariantId).ToHashSet();
        var missing = variantIds.Where(id => !known.Contains(id)).Cast<Guid?>().FirstOrDefault();
        if (missing is not null)
        {
            throw new WmsDomainException(WmsErrorCodes.ProfileVariantUnknown, $"Variant '{missing}' is not known to the catalog.");
        }
    }

    private async Task<List<AdjustmentLine>> BuildLinesAsync(
        ProposeAdjustmentCommand command, Warehouse warehouse, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var created = new Dictionary<(Guid VariantId, string Code), Lot>();
        var lines = new List<AdjustmentLine>(command.Lines.Count);
        foreach (var request in command.Lines)
        {
            var location = warehouse.FindLocation(new LocationId(request.LocationId)) ?? throw WmsNotFound.Location(request.LocationId);

            // Las virtuales son contrapartidas del ledger (ADJUSTMENT) o no son existencia real: una línea sobre ellas no se podría
            // contabilizar (from == to) o inventaría stock en tránsito, de proveedor o de cliente.
            if (location.IsVirtual)
            {
                throw new WmsDomainException(WmsErrorCodes.LocationVirtualImmutable, $"La ubicación '{location.Code}' es virtual y no admite ajustes.");
            }

            LotId? lotId = null;
            if (!string.IsNullOrWhiteSpace(request.LotCode))
            {
                var lot = await FindOrCreateLotAsync(command.TenantId, ownerId, request, created, now, cancellationToken);
                lotId = lot.Id;
            }

            lines.Add(AdjustmentLine.Create(
                location.Id,
                request.VariantId,
                lotId,
                Enum.Parse<StockStatus>(request.Status),
                request.QuantityDelta,
                request.CapturedUom,
                request.CapturedQty));
        }

        // La FK de la línea al lote no está en el modelo de EF, así que el orden de los INSERT no lo da el grafo: los lotes nuevos
        // se escriben antes de que exista el ajuste.
        if (created.Count > 0)
        {
            await unitOfWork.FlushAsync(cancellationToken);
        }

        return lines;
    }

    private async Task<Lot> FindOrCreateLotAsync(
        Guid tenantId,
        Guid ownerId,
        AdjustmentLineRequest request,
        Dictionary<(Guid VariantId, string Code), Lot> created,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var code = request.LotCode!.Trim();
        if (created.TryGetValue((request.VariantId, code), out var sameRequest))
        {
            return sameRequest;
        }

        var existing = await lots.FindAsync(tenantId, ownerId, request.VariantId, code, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // Sin perfil guardado la variante no controla vencimiento: el perfil por defecto nace con TracksExpiry apagado.
        var profile = await profiles.FindAsync(tenantId, request.VariantId, cancellationToken);
        var lot = Lot.Create(tenantId, ownerId, request.VariantId, code, request.ExpirationDate, null, profile?.TracksExpiry == true, now);
        lots.Add(lot);
        created[(request.VariantId, code)] = lot;
        return lot;
    }
}
