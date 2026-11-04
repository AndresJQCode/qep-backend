using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Api;

/// <summary>
/// Filtro del grupo público: sin la clave del storefront responde 401 <c>store.public.client_unauthorized</c> antes de que corra
/// cualquier handler. Corre DESPUÉS del limitador por IP (el middleware va antes del endpoint), así que los intentos con clave
/// inventada también cuentan contra el cupo de su IP. Nunca registra la clave presentada ni el secreto.
/// </summary>
public sealed partial class StorefrontKeyFilter(ILogger<StorefrontKeyFilter> logger) : IEndpointFilter
{
    // Sólo ruta e IP del par: la clave presentada y el secreto jamás van al log. Es lo que permite ver un storefront mal configurado.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Storefront call refused (401) for {Path} from {RemoteIp}")]
    private static partial void LogRefused(ILogger logger, string path, string remoteIp);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var secret = http.RequestServices.GetRequiredService<IOptions<StoreOptions>>().Value.Storefront.SharedSecret;
        if (!StorefrontClient.TryAuthenticate(http, secret))
        {
            LogRefused(logger, http.Request.Path.Value ?? string.Empty, http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized storefront client.",
                detail: "The storefront key is missing or invalid.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = StoreErrorCodes.PublicClientUnauthorized,
                    ["traceId"] = http.TraceIdentifier,
                });
        }

        http.Items[StorefrontClient.VisitorIpItem] = StorefrontClient.VisitorIp(http, secret);
        return await next(context);
    }
}
