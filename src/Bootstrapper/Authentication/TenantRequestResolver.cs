using Microsoft.AspNetCore.Http;

namespace Bootstrapper.Authentication;

/// <summary>
/// De dónde sale el tenant activo de un request. El header <c>X-Tenant-Id</c> manda, como siempre
/// (SDD-CT-12). El query <c>tenantId</c> sólo cuenta bajo <c>/api/v1/hubs/</c> (spec 2026-10-14,
/// Mensajería en tiempo real): el handshake WebSocket del navegador no puede llevar headers propios, y
/// en cualquier otra ruta aceptar el query abriría una segunda puerta que nadie pidió. Quien lo use
/// valida la membresía igual después, venga de donde venga: esto resuelve el id, no la autoridad.
/// <para>El path cuenta sólo en <c>GET /api/v1/tenants/{tenantId}/messaging/media/{messageId}</c>: ese
/// medio lo pide un <c>&lt;audio&gt;</c>, <c>&lt;img&gt;</c> o <c>&lt;video&gt;</c>, que tampoco puede llevar
/// headers, y sin tenant la sesión válida terminaba en 403 (bug 4 de PENDIENTES, 2026-10-15). Es el mismo
/// tenant que el endpoint ya recibe por la ruta, así que no abre una autoridad nueva.</para>
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

        if (TryResolveMediaPath(request, out tenantId))
        {
            return true;
        }

        tenantId = Guid.Empty;
        return false;
    }

    // ["api", "v1", "tenants", {tenantId}, "messaging", "media", {messageId}], y nada más.
    private static bool TryResolveMediaPath(HttpRequest request, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var segments = (request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["api", "v1", "tenants", var tenant, "messaging", "media", var message]
            && Guid.TryParse(message, out _)
            && Guid.TryParse(tenant, out tenantId);
    }
}
