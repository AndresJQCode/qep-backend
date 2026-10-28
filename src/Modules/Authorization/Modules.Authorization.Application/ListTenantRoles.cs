using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

public sealed record ListTenantRolesQuery(TenantId TenantId)
    : IQuery<IReadOnlyCollection<TenantRoleDefinition>>;

/// <summary>
/// Los roles que ve un tenant: los de sistema y los suyos, con la bandera que los distingue.
/// </summary>
/// <remarks>
/// Exige <c>advisorship.read</c> y no <c>advisorship.roles.manage</c>: mirar qué concede cada
/// rol es lo que necesita quien asigna roles a una persona, y esa capacidad es
/// <c>advisorship.manage</c>. Pedir el permiso de escritura para leer dejaría el editor de
/// roles de un miembro sin poder explicar qué está por conceder.
/// </remarks>
public sealed class ListTenantRolesHandler(
    ITenantRoleCatalog roleCatalog,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<ListTenantRolesQuery, IReadOnlyCollection<TenantRoleDefinition>>
{
    public async Task<IReadOnlyCollection<TenantRoleDefinition>> HandleAsync(
        ListTenantRolesQuery query,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId != query.TenantId ||
            !executionContext.HasPermission(TenancyPermissions.AdvisorshipRead))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read the roles of this tenant.");
        }

        var roles = await roleCatalog.ListRolesAsync(query.TenantId.Value, cancellationToken);
        // Spec 2026-10-08 §2: el admin de fábrica lleva operator.* y no tiene que aparecer en el
        // editor de otros tenants. Es la excepción a la regla de no filtrar lo guardado.
        var isOperator = operatorTenant.IsOperator(query.TenantId.Value);
        return roles
            .Select(role => role with { Permissions = OperatorPermissionFilter.Apply(role.Permissions, isOperator) })
            .ToArray();
    }
}
