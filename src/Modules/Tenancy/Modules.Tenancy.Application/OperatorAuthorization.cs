using BuildingBlocks.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Spec 2026-10-08 §5: segunda capa de los endpoints de operador. El tenant de la ruta es el del claim,
/// el permiso está en los claims <b>y</b> ese tenant es el operador. Va antes de buscar el destino para
/// que el 404 no le sirva de oráculo a quien no es operador. Cualquier falla: 403.
/// </summary>
internal static class OperatorAuthorization
{
    public static void EnsureAuthorized(
        IExecutionContext executionContext, TenantId tenantId, string permission, IOperatorTenant operatorTenant)
    {
        if (executionContext.TenantId != tenantId
            || !executionContext.HasPermission(permission)
            || !operatorTenant.IsOperator(tenantId.Value))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot operate the platform from this tenant.");
        }
    }
}
