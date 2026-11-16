using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>Lo que el catálogo del tenant sabe de una referencia de rol.</summary>
public enum RoleReference
{
    /// <summary>El catálogo del tenant no tiene ese rol: <c>tenancy.membership.role_unknown</c>.</summary>
    Unknown,

    /// <summary>
    /// Existe, pero no concede ningún permiso que el tenant ofrece (Cajero sin <c>pos</c>, spec 2026-11-15):
    /// no se puede agregar, aunque quien ya lo tiene lo conserva.
    /// </summary>
    Unassignable,

    Known,
}

/// <summary>
/// Valida que las referencias de rol de una membresía apunten a roles conocidos por la
/// capacidad Authorization, y que el rol conceda algo en el tenant. Tenancy es dueño de la
/// relación; Authorization es dueño del catálogo de roles.
/// </summary>
/// <remarks>
/// Por tenant y asíncrono desde los roles custom: «conocido» dejó de ser una propiedad del
/// build y pasó a ser una del tenant. Ver <see cref="IRolePermissionChecker"/>.
/// </remarks>
public interface IRoleReferenceValidator
{
    Task<RoleReference> CheckAsync(
        TenantId tenantId,
        string role,
        CancellationToken cancellationToken);
}
