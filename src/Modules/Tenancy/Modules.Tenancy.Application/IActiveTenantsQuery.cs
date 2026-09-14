namespace Modules.Tenancy.Application;

/// <summary>
/// Consulta de sólo lectura de las membresías activas de un usuario, publicada para que el
/// endpoint de revalidación de sesión <c>GET /auth/me</c> reconstruya la misma forma que
/// devuelve el flujo de login, sin mutar nada.
/// </summary>
public interface IActiveTenantsQuery
{
    /// <summary>
    /// Con el nombre de cada tenant, no sólo el id: lo necesita el selector de tenant del menú
    /// de usuario, que si sólo tuviera el id le haría adivinar a la persona cuál es cuál.
    /// Ordenado por <c>DisplayName</c> y, ante empate, por id, para que la lista sea
    /// determinista entre llamadas. Es la única consulta detrás de <c>SessionResponse</c>: los
    /// dos campos de la respuesta —ids y nombres— salen de esta misma llamada para que no
    /// puedan discrepar entre sí si una membresía cambia entre dos consultas separadas.
    /// </summary>
    Task<IReadOnlyCollection<ActiveTenantSummary>> ListActiveTenantsAsync(
        Guid userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lectura mínima de un tenant activo: el id y el nombre para mostrar, nada más.
/// </summary>
public sealed record ActiveTenantSummary(Guid TenantId, string DisplayName);
