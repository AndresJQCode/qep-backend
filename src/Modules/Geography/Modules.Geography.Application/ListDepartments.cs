using BuildingBlocks.Application;

namespace Modules.Geography.Application;

public sealed record ListDepartmentsQuery : IQuery<IReadOnlyList<DepartmentDto>>;

public sealed class ListDepartmentsHandler(IDepartmentRepository repository)
    : IQueryHandler<ListDepartmentsQuery, IReadOnlyList<DepartmentDto>>
{
    public async Task<IReadOnlyList<DepartmentDto>> HandleAsync(
        ListDepartmentsQuery query, CancellationToken cancellationToken)
    {
        var departments = await repository.ListAllAsync(cancellationToken);
        return departments
            .OrderBy(department => department.Name, StringComparer.OrdinalIgnoreCase)
            .Select(department => new DepartmentDto(
                department.Id.Value, department.DivipolaCode, department.Name))
            .ToArray();
    }
}
