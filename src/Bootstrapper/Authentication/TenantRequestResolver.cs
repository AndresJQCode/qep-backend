using Microsoft.AspNetCore.Http;

namespace Bootstrapper.Authentication;

/// <summary>
/// De dónde sale el tenant activo de un request. El header <c>X-Tenant-Id</c> manda, como siempre
/// (SDD-CT-12). El query <c>tenantId</c> sólo cuenta bajo <c>/api/v1/hubs/</c> (spec 2026-10-14,
/// Mensajería en tiempo real): el handshake WebSocket del navegador no puede llevar headers propios, y
/// en cualquier otra ruta aceptar el query abriría una segunda puerta que nadie pidió. Quien lo use
/// valida la membresía igual después, venga de donde venga: esto resuelve el id, no la autoridad.
/// </summary>
internal static class TenantRequestResolver
{
    private static readonly PathString HubsPrefix = new("/api/v1/hubs");

    public static bool TryResolve(HttpRequest? request, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (request is null)
        {
            return false;
        }

        if (Guid.TryParse(request.Headers["X-Tenant-Id"].ToString(), out tenantId))
        {
            return true;
        }

        if (request.Path.StartsWithSegments(HubsPrefix) && Guid.TryParse(request.Query["tenantId"].ToString(), out tenantId))
        {
            return true;
        }

        tenantId = Guid.Empty;
        return false;
    }
}
