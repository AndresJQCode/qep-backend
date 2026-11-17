using BuildingBlocks.Application;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 §7: cuánto del catálogo de la tienda ya tiene par en QEP, para el paso del onboarding. BFF: la pantalla dibuja
/// «X de Y variantes» y la lista de las que faltan sin consultar nada más. <see cref="Matched"/> cuenta una variante con vínculo o
/// cuyo SKU recortado ya es el código de una variante de QEP; <see cref="CatalogEmpty"/> es «no hay ningún producto activo»
/// (sugerir importar todo); <see cref="MissingSample"/> son las primeras 20 sin par, en el orden de Shopify.
/// </summary>
public sealed record ShopifyProductCoverageDto(int ShopifyVariants, int Matched, bool CatalogEmpty, IReadOnlyList<ShopifyMissingVariantDto> MissingSample);

/// <summary>Una variante sin par: su SKU recortado (<c>null</c> si no tiene) y el título de su producto.</summary>
public sealed record ShopifyMissingVariantDto(string? Sku, string Title);

/// <summary><c>GET …/stores/{connectionId}/products/coverage?redirectUri=</c> (<c>integrations.connection.read</c>).</summary>
public sealed record GetShopifyProductCoverageQuery(Guid TenantId, Guid ConnectionId, string? RedirectUri) : IQuery<ShopifyProductCoverageDto>;

/// <summary>
/// Consulta Shopify en el momento (todas las páginas de variantes) y no guarda nada (§7: cachear es optimización de después). Las
/// guardas son las del <c>POST</c> de la importación, sin la empresa. Una falla de Shopify: credencial rechazada → 422
/// <c>shopify.store.not_operational</c>; scope retirado → 422 <c>scope_missing</c>; sin respuesta o <c>THROTTLED</c> → 503
/// <c>shopify.store.sync_unavailable</c>.
/// </summary>
public sealed class GetShopifyProductCoverageHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IShopifyConnectionDirectory connections,
    IShopifyReauthorization reauthorization,
    IShopifyProductsSource source,
    IShopifyProductLinkRepository links,
    IShopifyProductLookup products) : IQueryHandler<GetShopifyProductCoverageQuery, ShopifyProductCoverageDto>
{
    public const int MissingSampleSize = 20;

    /// <summary>Los códigos se preguntan a Catalog en lotes, para no armar un <c>IN</c> de miles.</summary>
    public const int CodeBatchSize = 500;

    public async Task<ShopifyProductCoverageDto> HandleAsync(GetShopifyProductCoverageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await ShopifyAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, ShopifyPermissions.ConnectionRead, cancellationToken);
        var connection = await ShopifyStoreSupport.ConnectionAsync(connections, query.TenantId, query.ConnectionId, cancellationToken);
        ShopifyProductGuards.EnsureOperational(connection);
        await ShopifyProductGuards.EnsureReadProductsAsync(reauthorization, connection, query.RedirectUri, cancellationToken);

        var variants = new List<ShopifyVariantKey>();
        try
        {
            string? cursor = null;
            do
            {
                var page = await source.ListVariantKeysAsync(connection, cursor, cancellationToken);
                variants.AddRange(page.Variants);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        catch (ShopifyProductsSourceException exception)
        {
            throw exception.Failure switch
            {
                ShopifyProductsSourceFailure.Rejected => new ShopifyDomainException(
                    ShopifyErrorCodes.StoreNotOperational, "Shopify rejected the store credentials.", exception),
                ShopifyProductsSourceFailure.AccessDenied => new ShopifyProductsScopeMissingException(null),
                _ => new ServiceUnavailableException(
                    ShopifyErrorCodes.StoreSyncUnavailable, $"Shopify could not be read now ({exception.Failure}); try again later."),
            };
        }

        var linked = await links.ListLinkedShopifyVariantIdsAsync(connection.ConnectionId, cancellationToken);
        var candidates = variants
            .Where(variant => !linked.Contains(variant.VariantId))
            .Select(variant => Sku(variant))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in candidates.Chunk(CodeBatchSize))
        {
            existing.UnionWith(await products.FindExistingCodesAsync(query.TenantId, batch, cancellationToken));
        }

        var missing = variants
            .Where(variant => !linked.Contains(variant.VariantId) && !(Sku(variant) is { } sku && existing.Contains(sku)))
            .ToArray();
        var catalogEmpty = await products.CountActiveAsync(query.TenantId, cancellationToken) == 0;
        return new ShopifyProductCoverageDto(
            variants.Count,
            variants.Count - missing.Length,
            catalogEmpty,
            [.. missing.Take(MissingSampleSize).Select(variant => new ShopifyMissingVariantDto(Sku(variant), variant.ProductTitle))]);
    }

    private static string? Sku(ShopifyVariantKey variant) => string.IsNullOrWhiteSpace(variant.Sku) ? null : variant.Sku.Trim();
}
