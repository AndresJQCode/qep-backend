using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Api;

/// <summary>
/// Filtro del grupo público: sin la clave del storefront responde 401 <c>store.public.client_unauthorized</c> antes de que corra
/// cualquier handler. Corre DESPUÉS del limitador por IP (el middleware va antes del endpoint), así que los intentos con clave
/// inventada también cuentan contra el cupo de su IP. Nunca registra la clave presentada ni el secreto.
/// </summary>
public sealed class StorefrontKeyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var secret = http.RequestServices.GetRequiredService<IOptions<StoreOptions>>().Value.Storefront.SharedSecret;
        if (!StorefrontClient.TryAuthenticate(http, secret))
        {
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
