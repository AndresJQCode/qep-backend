using Modules.Tenancy.Application;

namespace Modules.Authorization.UnitTests;

internal sealed class FixedOperatorTenant(Guid? operatorTenantId) : IOperatorTenant
{
    public bool IsOperator(Guid tenantId) => operatorTenantId == tenantId;
}
