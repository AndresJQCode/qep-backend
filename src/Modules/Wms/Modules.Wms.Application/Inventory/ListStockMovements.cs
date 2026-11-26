using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Wms.Application;

/// <summary><c>Limit</c> va de 1 a 200 (50 si el cliente no lo manda); <c>Cursor</c> nulo arranca desde el movimiento más reciente.</summary>
public sealed record ListStockMovementsQuery(
    Guid TenantId,
    Guid? VariantId,
    Guid? LocationId,
    Guid? LotId,
    string? Serial,
    string? DocumentType,
    Guid? DocumentId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Cursor,
    int Limit) : IQuery<MovementPageDto>
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>El cursor que no salió de <see cref="MovementCursor.Encode"/> es un 422 <c>validation.failed</c> con la clave <c>Cursor</c>,
/// el mismo mapa <c>errors</c> que el formulario sabe leer.</summary>
public sealed class ListStockMovementsValidator : AbstractValidator<ListStockMovementsQuery>
{
    public ListStockMovementsValidator()
    {
        RuleFor(query => query.Cursor)
            .Must(value => MovementCursor.TryDecode(value, out _)).When(query => query.Cursor is not null)
            .WithMessage("The cursor is not one issued by this list.");
        RuleFor(query => query.Limit).InclusiveBetween(1, ListStockMovementsQuery.MaxLimit);
        RuleFor(query => query.Serial).MaximumLength(128);
        RuleFor(query => query.DocumentType).MaximumLength(64);
    }
}

/// <summary>La historia del ledger (RF-022), por cursor sobre <c>(occurred_at, id)</c> descendente. Sin <c>COUNT(*)</c>: el ledger
/// crece sin tope. El producto y su código salen de Catalog y la unidad base del perfil.</summary>
public sealed class ListStockMovementsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IStockReadQueries queries,
    IWmsCatalogReader catalog,
    IVariantProfileRepository profiles,
    IValidator<ListStockMovementsQuery> validator)
    : IQueryHandler<ListStockMovementsQuery, MovementPageDto>
{
    public async Task<MovementPageDto> HandleAsync(ListStockMovementsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(executionContext, query.TenantId, WmsPermissions.StockRead, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        MovementCursor? after = null;
        if (query.Cursor is not null && MovementCursor.TryDecode(query.Cursor, out var decoded))
        {
            after = decoded;
        }

        var filter = new MovementFilter(
            query.TenantId,
            query.VariantId,
            query.LocationId,
            query.LotId,
            string.IsNullOrWhiteSpace(query.Serial) ? null : query.Serial.Trim(),
            string.IsNullOrWhiteSpace(query.DocumentType) ? null : query.DocumentType.Trim(),
            query.DocumentId,
            query.From,
            query.To,
            after,
            query.Limit);

        var result = await queries.ListMovementsAsync(filter, cancellationToken);

        var variantIds = result.Rows.Select(row => row.VariantId).Distinct().ToList();
        var snapshots = (await catalog.GetVariantsAsync(query.TenantId, variantIds, cancellationToken)).ToDictionary(snapshot => snapshot.VariantId);
        var baseUoms = (await profiles.ListAsync(query.TenantId, variantIds, cancellationToken))
            .ToDictionary(profile => profile.VariantId, profile => profile.BaseUom.Value);

        var items = result.Rows.Select(row => new MovementDto(
            row.Id,
            row.OccurredAt,
            row.RecordedAt,
            row.Reason,
            row.Quantity,
            baseUoms.GetValueOrDefault(row.VariantId) ?? WmsDefaults.BaseUom,
            row.CapturedUom,
            row.CapturedQty,
            row.ConversionFactor,
            new MovementEndpointDto(row.FromLocationCode, row.FromStatus),
            new MovementEndpointDto(row.ToLocationCode, row.ToStatus),
            row.VariantId,
            snapshots.GetValueOrDefault(row.VariantId)?.ProductName ?? string.Empty,
            snapshots.GetValueOrDefault(row.VariantId)?.Code ?? string.Empty,
            row.LotCode,
            row.Serial,
            row.DocumentType,
            row.DocumentId,
            DocumentNumber: null,
            row.BatchId,
            row.ReversesMovementId,
            row.ActorId,
            ActorName: null,
            row.ActorType,
            row.DeviceId)).ToList();

        var next = result.HasMore && result.Rows.Count > 0
            ? MovementCursor.Encode(new MovementCursor(result.Rows[^1].OccurredAt, result.Rows[^1].Id))
            : null;
        return new MovementPageDto(items, next);
    }
}
