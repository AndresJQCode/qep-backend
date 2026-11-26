using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Control;

namespace Modules.Wms.Application;

public sealed record ListAdjustmentsQuery(Guid TenantId, string? Status, Guid? WarehouseId, int Page, int PageSize) : IQuery<AdjustmentPageDto>;

/// <summary>Un estado que no es un nombre exacto del enum es un 422 <c>validation.failed</c> con la clave <c>Status</c>.</summary>
public sealed class ListAdjustmentsValidator : AbstractValidator<ListAdjustmentsQuery>
{
    public ListAdjustmentsValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => Enum.GetNames<AdjustmentStatus>().Contains(status, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Status))
            .WithMessage($"The status must be one of: {string.Join(", ", Enum.GetNames<AdjustmentStatus>())}.");
    }
}

public sealed class ListAdjustmentsHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IAdjustmentRepository adjustments,
    AdjustmentDtoComposer composer,
    IValidator<ListAdjustmentsQuery> validator)
    : IQueryHandler<ListAdjustmentsQuery, AdjustmentPageDto>
{
    public async Task<AdjustmentPageDto> HandleAsync(ListAdjustmentsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var page = WmsPaging.NormalizePage(query.Page);
        var pageSize = WmsPaging.NormalizePageSize(query.PageSize);
        AdjustmentStatus? status = string.IsNullOrWhiteSpace(query.Status) ? null : Enum.Parse<AdjustmentStatus>(query.Status);

        var result = await adjustments.ListAsync(query.TenantId, status, query.WarehouseId, page, pageSize, cancellationToken);
        var items = await composer.ComposeManyAsync(query.TenantId, result.Items, cancellationToken);
        return new AdjustmentPageDto(items, page, pageSize, result.TotalCount);
    }
}
