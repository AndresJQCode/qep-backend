using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Api;

/// <summary>
/// El formulario público de Soporte (spec §9, plan F3 a F6): rutas anónimas que la página de <c>soporte.lulocrm.com</c> llama directo
/// desde el navegador, sin BFF. Sin sesión y sin CSRF (excepción por prefijo en Bootstrapper, <c>SupportPublicPaths</c>, que una prueba
/// ata a <see cref="GroupPattern"/>); con la política CORS propia sin credenciales (<see cref="CorsPolicyName"/>), el tope del cuerpo
/// de 64 KB y los limitadores por IP del visitante. El <c>Cache-Control: no-store</c> de toda respuesta lo pone
/// <c>BuyerNoStoreMiddleware</c>, antes del limitador. Ningún handler del grupo lee el contexto de ejecución ni la cookie.
/// </summary>
public static class PublicSupportEndpoints
{
    /// <summary>El patrón del grupo. Bootstrapper duplica el prefijo (<c>SupportPublicPaths.Prefix</c>): <c>SupportPublicPathsTests</c>
    /// los ata.</summary>
    public const string GroupPattern = "/api/v1/public/support/{tenantSlug}";

    /// <summary>La política CORS del grupo, registrada en Bootstrapper (<c>CorsServiceCollectionExtensions</c>).</summary>
    public const string CorsPolicyName = "support-public";

    /// <summary>Tope del cuerpo de cualquier ruta del grupo: un radicado con todos los campos cabe de sobra y el resto es abuso.</summary>
    private const long MaxRequestBodyBytes = 65_536;

    private const int ReadChunkSize = 16 * 1024;

    /// <param name="readPolicy">Configuración y geografía: el limitador del grupo.</param>
    /// <param name="submitPolicy">Radicar y reenviar el PIN.</param>
    /// <param name="lookupPolicy">Consultar un ticket.</param>
    public static IEndpointRouteBuilder MapPublicSupportEndpoints(
        this IEndpointRouteBuilder endpoints, string readPolicy, string submitPolicy, string lookupPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(submitPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupPolicy);

        var group = endpoints
            .MapGroup(GroupPattern)
            .WithTags("Support public")
            .AllowAnonymous()
            .RequireCors(CorsPolicyName)
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireRateLimiting(readPolicy);

        group.MapGet("/form", GetFormAsync)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/tickets/lookup", LookupAsync)
            .RequireRateLimiting(lookupPolicy)
            .Accepts<SupportPublicLookupRequest>("application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    // Task 4: el grupo y su fontanería. La configuración pública llega en la Task 5; hasta entonces todo tenant responde 404.
    private static async Task<IResult> GetFormAsync(
        string tenantSlug, SupportPublicResolver resolver, CancellationToken cancellationToken)
    {
        _ = await resolver.ResolveAsync(tenantSlug, cancellationToken);
        throw new ResourceNotFoundException(SupportErrorCodes.PublicNotFound, "The support form was not found.");
    }

    // Task 4: lee el cuerpo con su tope y resuelve el tenant. La consulta llega en la Task 7.
    private static async Task<IResult> LookupAsync(
        string tenantSlug, SupportPublicResolver resolver, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var (body, rejection) = await ReadBodyAsync<SupportPublicLookupRequest>(httpContext, cancellationToken);
        if (body is null)
        {
            return rejection!;
        }

        _ = await resolver.ResolveAsync(tenantSlug, cancellationToken);
        throw new ResourceNotFoundException(SupportErrorCodes.PublicNotFound, "The support form was not found.");
    }

    // Copia del lector de PublicBuyerEndpoints: el tope se aplica leyendo, porque RequestSizeLimitAttribute es sólo metadato que
    // Kestrel convierte en límite, y un servidor que no lo hace o un cuerpo sin Content-Length lo dejarían pasar. Un JSON ilegible o
    // null es un 400 (el binder de minimal API lo dejaría salir como 500 por el manejador de excepciones).
    private static async Task<(T? Body, IResult? Rejection)> ReadBodyAsync<T>(
        HttpContext httpContext, CancellationToken cancellationToken)
        where T : class
    {
        var stream = httpContext.Request.Body;
        if (httpContext.Request.ContentLength is { } declared && declared > MaxRequestBodyBytes)
        {
            return (null, Rejected(StatusCodes.Status413PayloadTooLarge));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkSize];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxRequestBodyBytes)
                {
                    return (null, Rejected(StatusCodes.Status413PayloadTooLarge));
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException exception)
        {
            return (null, Rejected(exception.StatusCode));
        }

        var options = httpContext.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        try
        {
            var body = JsonSerializer.Deserialize<T>(buffer.ToArray(), options);
            return body is null ? (null, Rejected(StatusCodes.Status400BadRequest)) : (body, null);
        }
        catch (JsonException)
        {
            return (null, Rejected(StatusCodes.Status400BadRequest));
        }
    }

    private static IResult Rejected(int status) =>
        Results.Problem(
            statusCode: status,
            title: "The request body is not acceptable.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = status == StatusCodes.Status413PayloadTooLarge
                    ? SupportErrorCodes.PublicBodyTooLarge
                    : SupportErrorCodes.PublicBodyInvalid,
            });
}
