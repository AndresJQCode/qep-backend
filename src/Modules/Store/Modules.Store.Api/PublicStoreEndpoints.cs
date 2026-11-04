using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Api;

public static class PublicStoreEndpoints
{
    /// <summary>Tope del cuerpo de cualquier ruta pública: el carrito más grande cabe de sobra y el resto es abuso.</summary>
    private const long MaxRequestBodyBytes = 65_536;

    // Lecturas del catálogo: un minuto fresco y cinco más sirviendo lo viejo mientras revalida. Un precio cambiado tarda a lo sumo un
    // minuto en verse, y el carrito (POST) nunca se cachea.
    private const string CatalogCacheControl = "public, max-age=60, stale-while-revalidate=300";

    // La geografía de Colombia casi no cambia.
    private const string GeographyCacheControl = "public, max-age=3600, stale-while-revalidate=86400";

    /// <param name="readPolicy">Limitador de las lecturas y del carrito (por IP del visitante).</param>
    /// <param name="submitPolicy">Limitador del envío de solicitudes: <c>order-requests</c> reemplaza <paramref name="readPolicy"/> por este.</param>
    public static IEndpointRouteBuilder MapPublicStoreEndpoints(
        this IEndpointRouteBuilder endpoints, string readPolicy, string submitPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(readPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(submitPolicy);

        // Sin sesión: el host de la tienda va en la ruta y nunca se lee la cookie. X-Qep-Client y CSRF no aplican a las rutas
        // públicas (excepción por prefijo en RequireCsrfHeaderMiddleware); en su lugar, el filtro exige la clave del storefront.
        var group = endpoints
            .MapGroup("/api/v1/public/stores/{host}")
            .WithTags("Store")
            .AllowAnonymous()
            .AddEndpointFilter<StorefrontKeyFilter>()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireRateLimiting(readPolicy);

        group.MapGet("/", GetStoreAsync)
            .Produces<PublicStoreDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/categories", ListCategoriesAsync)
            .Produces<PublicCategoriesDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/products", ListProductsAsync)
            .Produces<PublicProductsPageDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/products/{productId:guid}", GetProductAsync)
            .Produces<PublicProductDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // POST pero una lectura: cotiza, no guarda. Sin caché.
        group.MapPost("/cart/quote", QuoteCartAsync)
            .Produces<PublicCartQuoteDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/geography/departments", ListDepartmentsAsync)
            .Produces<PublicGeoListDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/geography/departments/{departmentId:guid}/cities", ListCitiesAsync)
            .Produces<PublicGeoListDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // El limitador del endpoint reemplaza al del grupo: el envío cuenta contra su propio cupo (5 cada 10 min), no contra las lecturas.
        group.MapPost("/order-requests", SubmitOrderRequestAsync)
            .RequireRateLimiting(submitPolicy)
            .Produces<PublicOrderRequestReceiptDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    // 201 tanto para la primera vez como para el reintento con el mismo clientRequestId: el visitante no necesita distinguirlos.
    private static async Task<IResult> SubmitOrderRequestAsync(
        string host,
        PublicOrderRequestBody body,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // La IP del consentimiento es la que declara el storefront con su clave, nunca la del par directo (que sería la del servidor de
        // la tienda): sin ella no hay consentimiento que guardar.
        var secret = httpContext.RequestServices.GetRequiredService<StoreOptions>().Storefront.SharedSecret;
        if (!StorefrontClient.TryGetVisitorIp(httpContext, secret, out var visitorIp))
        {
            throw new StoreDomainException(
                StoreErrorCodes.PublicVisitorIpRequired, "The storefront must forward the visitor IP to submit an order request.");
        }

        var (receipt, _) = await dispatcher.SendAsync(
            new SubmitOrderRequestCommand(host, visitorIp, NormalizeLines(body)), cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(receipt, statusCode: StatusCodes.Status201Created);
    }

    // Un cuerpo con lineas ausentes o nulas llega al validador como un carrito invalido (422), no como un 500.
    private static PublicOrderRequestBody NormalizeLines(PublicOrderRequestBody body) =>
        body with
        {
            Lines = (body.Lines ?? []).Select(line => line ?? new PublicCartLineRequest(Guid.Empty, 0)).ToList(),
        };

    private static async Task<IResult> GetStoreAsync(
        string host, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Cached(httpContext, CatalogCacheControl, await dispatcher.QueryAsync(new GetPublicStoreQuery(host), cancellationToken));

    private static async Task<IResult> ListCategoriesAsync(
        string host, string? currency, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Cached(
            httpContext,
            CatalogCacheControl,
            await dispatcher.QueryAsync(new ListPublicCategoriesQuery(host, currency), cancellationToken));

    private static async Task<IResult> ListProductsAsync(
        string host,
        string? currency,
        Guid? category,
        string? q,
        int? page,
        int? pageSize,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Cached(
            httpContext,
            CatalogCacheControl,
            await dispatcher.QueryAsync(
                new ListPublicProductsQuery(host, currency, category, q, page ?? 1, pageSize ?? 0), cancellationToken));

    private static async Task<IResult> GetProductAsync(
        string host,
        Guid productId,
        string? currency,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Cached(
            httpContext,
            CatalogCacheControl,
            await dispatcher.QueryAsync(new GetPublicProductQuery(host, productId, currency), cancellationToken));

    private static async Task<IResult> QuoteCartAsync(
        string host, PublicCartRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        // Un cuerpo con líneas ausentes o nulas llega al validador como un carrito inválido (422), no como un 500.
        var lines = (request.Lines ?? [])
            .Select(line => line ?? new PublicCartLineRequest(Guid.Empty, 0))
            .ToList();
        return Results.Ok(await dispatcher.QueryAsync(new QuotePublicCartQuery(host, request.Currency, lines), cancellationToken));
    }

    private static async Task<IResult> ListDepartmentsAsync(
        string host, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Cached(
            httpContext,
            GeographyCacheControl,
            await dispatcher.QueryAsync(new ListPublicDepartmentsQuery(host), cancellationToken));

    private static async Task<IResult> ListCitiesAsync(
        string host,
        Guid departmentId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Cached(
            httpContext,
            GeographyCacheControl,
            await dispatcher.QueryAsync(new ListPublicCitiesQuery(host, departmentId), cancellationToken));

    // Se fija después de despachar: un 404 o un 422 (que lanzan antes de llegar acá) nunca llevan la cabecera de caché.
    private static IResult Cached<T>(HttpContext httpContext, string cacheControl, T body)
    {
        httpContext.Response.Headers.CacheControl = cacheControl;
        return Results.Ok(body);
    }
}
