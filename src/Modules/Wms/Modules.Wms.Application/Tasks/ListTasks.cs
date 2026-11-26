using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record ListTasksQuery(
    Guid TenantId, Guid? WarehouseId, string? Status, string? Type, Guid? AssignedMemberId, int Page, int PageSize)
    : IQuery<TaskPageDto>;

/// <summary>Un estado o un tipo que no es un nombre exacto del enum es un 422 <c>validation.failed</c> con su campo.</summary>
public sealed class ListTasksValidator : AbstractValidator<ListTasksQuery>
{
    public ListTasksValidator()
    {
        RuleFor(query => query.Status)
            .Must(status => Enum.GetNames<WarehouseTaskStatus>().Contains(status, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Status))
            .WithMessage($"The status must be one of: {string.Join(", ", Enum.GetNames<WarehouseTaskStatus>())}.");
        RuleFor(query => query.Type)
            .Must(type => Enum.GetNames<WarehouseTaskType>().Contains(type, StringComparer.Ordinal))
            .When(query => !string.IsNullOrWhiteSpace(query.Type))
            .WithMessage($"The type must be one of: {string.Join(", ", Enum.GetNames<WarehouseTaskType>())}.");
    }
}

public sealed class ListTasksHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWarehouseTaskRepository tasks,
    WarehouseTaskDtoComposer composer,
    IValidator<ListTasksQuery> validator)
    : IQueryHandler<ListTasksQuery, TaskPageDto>
{
    public async Task<TaskPageDto> HandleAsync(ListTasksQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.DocumentRead, warehouseId: query.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var page = WmsPaging.NormalizePage(query.Page);
        var pageSize = WmsPaging.NormalizePageSize(query.PageSize);
        WarehouseTaskStatus? status = string.IsNullOrWhiteSpace(query.Status) ? null : Enum.Parse<WarehouseTaskStatus>(query.Status);
        WarehouseTaskType? type = string.IsNullOrWhiteSpace(query.Type) ? null : Enum.Parse<WarehouseTaskType>(query.Type);

        var result = await tasks.ListAsync(
            query.TenantId, query.WarehouseId, status, type, query.AssignedMemberId, page, pageSize, cancellationToken);
        var items = await composer.ComposeSummariesAsync(query.TenantId, result.Items, cancellationToken);
        return new TaskPageDto(items, page, pageSize, result.TotalCount);
    }
}
