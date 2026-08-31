namespace Modules.Tenancy.Application;

/// <summary>
/// Consulta de sólo lectura que se usa en cada request para validar que un usuario tiene una
/// membresía activa en el tenant pedido y para obtener sus referencias de rol. Un id de
/// tenant que llega en un token o header es sólo una señal; el acceso se valida acá contra
/// una membresía viva (según los deep-dives de tenancy).
/// </summary>
public interface IMembershipDirectory
{
    /// <returns>Las referencias de rol de la membresía activa, o <c>null</c> si el usuario
    /// no tiene membresía activa en el tenant.</returns>
    Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// El id de la membresía activa del usuario en el tenant, o <c>null</c> si no tiene una. Lo
    /// usan módulos de negocio (p. ej. Quotations, ADR de "las referencias de usuario van a
    /// members") que necesitan grabar *cuál* membresía hizo algo, no sólo sus permisos.
    /// </summary>
    Task<Guid?> FindActiveMembershipIdAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Los ids de <b>todas</b> las membresías del usuario, en cualquier tenant y en cualquier
    /// estado, quitadas incluidas. Es la traducción inversa de
    /// <see cref="FindActiveMembershipIdAsync"/>: un módulo que grabó <c>MemberId</c>s y tiene
    /// que responder "¿este usuario me dejó huella?" (su <c>IUserReferenceProbe</c>) no puede
    /// acotarse a las activas, porque una cotización referencia a la membresía con la que se
    /// hizo aunque después la hayan quitado.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
        Guid userId,
        CancellationToken cancellationToken);
}
