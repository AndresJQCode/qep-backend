using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public interface ICityRepository
{
    Task<IReadOnlyList<City>> ListByDepartmentAsync(
        DepartmentId departmentId, CancellationToken cancellationToken);
}
