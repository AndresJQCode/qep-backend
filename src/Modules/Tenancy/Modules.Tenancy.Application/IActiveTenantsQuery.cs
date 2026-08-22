namespace Modules.Tenancy.Application;

/// <summary>
/// Consulta de sólo lectura de las membresías activas de un usuario, publicada para que el
/// endpoint de revalidación de sesión <c>GET /auth/me</c> reconstruya la misma forma que
/// devuelve el flujo de login, sin mutar nada.
/// </summary>
public interface IActiveTenantsQuery
{
    Task<IReadOnlyCollection<Guid>> ListActiveTenantIdsAsync(
        Guid userId,
        CancellationToken cancellationToken);
}
