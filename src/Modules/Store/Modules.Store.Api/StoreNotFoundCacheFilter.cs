using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Modules.Store.Domain;

namespace Modules.Store.Api;

/// <summary>
/// Filtro del grupo público: el 404 <c>store.public.not_found</c> de un GET sale con
/// <see cref="PublicStoreEndpoints.StoreNotFoundCacheControl"/>, para que el storefront y cualquier intermediario guarden la tienda
/// inexistente lo que dice el backend y no lo que suponga cada uno. Ningún otro error cambia: el resto de los 404, los 422 y los 5xx
/// siguen saliendo con el <c>no-store, no-cache</c> del manejador de excepciones, y el 401 de la clave no llega hasta acá.
/// </summary>
/// <remarks>
/// La excepción sigue viajando al manejador central, que arma el problema, lo registra y lo responde como siempre. La cabecera no se
/// puede fijar ahí ni acá directamente: <c>UseExceptionHandler</c> limpia la respuesta y registra un <c>OnStarting</c> que pisa
/// Cache-Control. Los <c>OnStarting</c> corren en orden inverso al registro, así que el de este filtro —registrado antes de que la
/// excepción llegue al middleware— corre después del suyo y es el que queda. Es el mismo mecanismo por el que CORS sobrevive a la
/// limpieza (ver <c>Program.cs</c>).
/// </remarks>
public sealed class StoreNotFoundCacheFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            return await next(context);
        }
        catch (ResourceNotFoundException exception)
            when (exception.Code == StoreErrorCodes.PublicNotFound && HttpMethods.IsGet(context.HttpContext.Request.Method))
        {
            context.HttpContext.Response.OnStarting(
                static state =>
                {
                    var response = (HttpResponse)state;
                    if (response.StatusCode == StatusCodes.Status404NotFound)
                    {
                        response.Headers.CacheControl = PublicStoreEndpoints.StoreNotFoundCacheControl;
                        response.Headers.Remove("Pragma");
                        response.Headers.Remove("Expires");
                    }

                    return Task.CompletedTask;
                },
                context.HttpContext.Response);
            throw;
        }
    }
}
