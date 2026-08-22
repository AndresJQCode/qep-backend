using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public interface IExecutionContext
{
    Guid SubjectId { get; }

    TenantId TenantId { get; }

    bool HasPermission(string permission);
}
