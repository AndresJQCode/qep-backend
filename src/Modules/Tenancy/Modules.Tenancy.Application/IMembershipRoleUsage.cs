using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Qué roles están efectivamente en uso por las membresías activas de un tenant.
/// </summary>
/// <remarks>
/// Es propiedad de Tenancy —dueño de la relación miembro/rol— y lo consume Authorization
/// para dos guardas que sin esto no se pueden hacer cumplir: no borrar un rol que alguien
/// tiene puesto, y no dejar al tenant sin nadie capaz de administrar. Misma dirección que
/// <see cref="IMembershipDirectory"/>: Tenancy declara el contrato, otro módulo lo consume.
///
/// Devuelve los conjuntos y no una lista aplanada porque la segunda pregunta lo necesita:
/// saber si alguien conserva un permiso exige evaluar los roles de **cada persona** juntos,
/// y una lista aplanada de todos los roles del tenant ya perdió esa agrupación.
/// </remarks>
public interface IMembershipRoleUsage
{
    Task<IReadOnlyCollection<IReadOnlyCollection<string>>> ActiveRoleSetsAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);
}
