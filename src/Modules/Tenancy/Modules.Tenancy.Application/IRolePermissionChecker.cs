using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Resuelve si alguna de un conjunto de referencias de rol de membresía concede un permiso
/// dado. Es propiedad de Tenancy para que los invariantes de membresía (por ejemplo, que un
/// tenant conserve un miembro capaz de gestionar membresías) se puedan hacer cumplir sin que
/// Tenancy dependa de los internos del catálogo de roles de Authorization; lo implementa
/// Authorization contra su propio catálogo, en la misma dirección del contrato
/// <see cref="IMembershipDirectory"/> (fronteras de módulo del ADR 0016).
/// </summary>
/// <remarks>
/// Recibe el tenant y es asíncrono desde que existen los roles custom: el catálogo dejó de
/// ser una constante del proceso y pasó a depender de la organización que pregunta. Sin el
/// tenant, un rol definido en otra concedería permisos acá.
/// </remarks>
public interface IRolePermissionChecker
{
    Task<bool> AnyGrantsAsync(
        TenantId tenantId,
        IReadOnlyCollection<string> roles,
        string permission,
        CancellationToken cancellationToken);
}
