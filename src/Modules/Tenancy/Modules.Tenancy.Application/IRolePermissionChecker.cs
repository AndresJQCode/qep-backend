namespace Modules.Tenancy.Application;

/// <summary>
/// Resuelve si alguna de un conjunto de referencias de rol de membresía concede un permiso
/// dado. Es propiedad de Tenancy para que los invariantes de membresía (por ejemplo, que un
/// tenant conserve un miembro capaz de gestionar membresías) se puedan hacer cumplir sin que
/// Tenancy dependa de los internos del catálogo de roles de Authorization; lo implementa
/// Authorization contra su propio <c>IRoleCatalog</c>, en la misma dirección del contrato
/// <see cref="IMembershipDirectory"/> (fronteras de módulo del ADR 0016).
/// </summary>
public interface IRolePermissionChecker
{
    bool AnyGrants(IReadOnlyCollection<string> roles, string permission);
}
