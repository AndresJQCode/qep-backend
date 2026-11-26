namespace Modules.Tenancy.Domain;

/// <summary>
/// Slugs que ningún tenant nuevo puede tomar: son las rutas fijas del host del formulario
/// público de Soporte (<c>soporte.lulocrm.com/{tenantSlug}</c>, spec 2026-11-14 §9.2). Un tenant
/// llamado <c>consultar</c> taparía la página de consulta; <c>calificar</c> reserva la encuesta
/// de la fase 4.
/// </summary>
/// <remarks>
/// Se verifica sólo al crear y al registrar, nunca en el constructor que usa EF: un tenant que
/// ya existía con uno de estos slugs sigue cargando y se corrige a mano. Antes de desplegar se
/// consulta en producción (README § Soporte).
/// </remarks>
public static class TenantSlugReservations
{
    public const string ReservedCode = "tenancy.slug.reserved";

    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "api",
        "app",
        "assets",
        "static",
        "public",
        "consultar",
        "calificar",
        "admin",
        "www",
        "soporte",
        "support",
        "pqrsf",
        "qep",
        "login",
        "cuenta",
        "health",
        "status",
        "robots",
        "sitemap",
    };

    /// <summary>Compara en ordinal: recibe el slug ya normalizado (recortado y en minúsculas).</summary>
    public static bool IsReserved(string normalizedSlug) => Reserved.Contains(normalizedSlug);
}
