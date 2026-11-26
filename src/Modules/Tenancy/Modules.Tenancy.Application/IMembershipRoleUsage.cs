using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Qué roles están en uso por las membresías de un tenant.
/// </summary>
/// <remarks>
/// Es propiedad de Tenancy —dueño de la relación miembro/rol— y lo consume Authorization
/// para dos guardas que sin esto no se pueden hacer cumplir: no borrar un rol que alguien
/// tiene puesto, y no dejar al tenant sin nadie capaz de administrar. Misma dirección que
/// <see cref="IMembershipDirectory"/>: Tenancy declara el contrato, otro módulo lo consume.
///
/// Son dos preguntas con alcances distintos. Para administrar sólo cuentan las activas: las
/// demás no pueden entrar. Para borrar un rol cuenta cualquier membresía que todavía dé o
/// prometa acceso, porque seguiría nombrando un rol que ya no existe.
///
/// <see cref="ActiveRoleSetsAsync"/> devuelve los conjuntos y no una lista aplanada porque su
/// pregunta lo necesita: saber si alguien conserva un permiso exige evaluar los roles de
/// **cada persona** juntos, y una lista aplanada de todos los roles del tenant ya perdió esa
/// agrupación.
/// </remarks>
public interface IMembershipRoleUsage
{
    Task<IReadOnlyCollection<IReadOnlyCollection<string>>> ActiveRoleSetsAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>true</c> si alguna membresía del tenant invitada (aunque la invitación ya haya vencido sin marcarse), activa o
    /// suspendida lleva el rol. Las quitadas y las vencidas no cuentan.
    /// </summary>
    Task<bool> AnyHoldsRoleAsync(
        TenantId tenantId,
        string role,
        CancellationToken cancellationToken);
}
