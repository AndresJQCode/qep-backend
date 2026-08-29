using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Valida que las referencias de rol de una membresía apunten a roles conocidos por la
/// capacidad Authorization. Tenancy es dueño de la relación; Authorization es dueño del
/// catálogo de roles.
/// </summary>
/// <remarks>
/// Por tenant y asíncrono desde los roles custom: «conocido» dejó de ser una propiedad del
/// build y pasó a ser una del tenant. Ver <see cref="IRolePermissionChecker"/>.
/// </remarks>
public interface IRoleReferenceValidator
{
    Task<bool> IsKnownRoleAsync(
        TenantId tenantId,
        string role,
        CancellationToken cancellationToken);
}
