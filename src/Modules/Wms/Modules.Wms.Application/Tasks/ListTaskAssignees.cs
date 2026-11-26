using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Application;

public sealed record ListTaskAssigneesQuery(Guid TenantId, Guid WarehouseId, string? Type) : IQuery<TaskAssigneesDto>;

public sealed class ListTaskAssigneesValidator : AbstractValidator<ListTaskAssigneesQuery>
{
    public ListTaskAssigneesValidator() =>
        RuleFor(query => query.Type)
            .Must(type => Enum.GetNames<WarehouseTaskType>().Contains(type, StringComparer.Ordinal))
            .WithMessage($"The type must be one of: {string.Join(", ", Enum.GetNames<WarehouseTaskType>())}.");
}

/// <summary>El selector de asignado (spec §6.10): las membresías activas con el permiso del tipo, por nombre. La bodega sólo acota la
/// autorización hasta que haya alcance por bodega.</summary>
public sealed class ListTaskAssigneesHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsMemberDirectory members,
    IValidator<ListTaskAssigneesQuery> validator)
    : IQueryHandler<ListTaskAssigneesQuery, TaskAssigneesDto>
{
    public async Task<TaskAssigneesDto> HandleAsync(ListTaskAssigneesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await authorization.EnsureAsync(
            executionContext, query.TenantId, WmsPermissions.TaskManage, warehouseId: query.WarehouseId, ct: cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var permission = TaskTypePermissions.For(Enum.Parse<WarehouseTaskType>(query.Type!));
        var ids = await members.ListWithPermissionAsync(query.TenantId, permission, cancellationToken);
        var names = await members.FindNamesAsync(query.TenantId, ids, cancellationToken);

        return new TaskAssigneesDto(
        [
            .. ids
                .Select(id => new TaskAssigneeDto(id, names.GetValueOrDefault(id) ?? "Sin nombre"))
                .OrderBy(item => item.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(item => item.MemberId),
        ]);
    }
}
