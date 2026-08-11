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
}
