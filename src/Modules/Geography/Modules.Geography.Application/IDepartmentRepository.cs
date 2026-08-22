using Modules.Geography.Domain;

namespace Modules.Geography.Application;

public interface IDepartmentRepository
{
    Task<IReadOnlyList<Department>> ListAllAsync(CancellationToken cancellationToken);
}
