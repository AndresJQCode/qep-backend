using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Modules.Store.Application;

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
        // La ruta del envío de solicitudes llega con su tarea y usa submitPolicy.
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

        return endpoints;
    }

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
