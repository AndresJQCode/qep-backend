using BuildingBlocks.Domain;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-11-17 §5.1 y plan D6: 422 <c>shopify.products.scope_missing</c> con <c>reauthorizeUrl</c>, la URL para volver a
/// autorizar la app con los scopes de hoy. Es <c>null</c> si el cliente no mandó <c>redirectUri</c>: la extensión viaja igual, para
/// que la pantalla no tenga que distinguir «sin URL» de «sin campo».
/// </summary>
public sealed class ShopifyProductsScopeMissingException(string? reauthorizeUrl)
    : DomainException(ShopifyErrorCodes.ProductsScopeMissing, "The Shopify store did not grant read_products; reauthorize the app."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } =
        new Dictionary<string, object?> { ["reauthorizeUrl"] = reauthorizeUrl };
}

/// <summary>Las guardas que comparten el <c>POST</c> de la importación y la cobertura.</summary>
internal static class ShopifyProductGuards
{
    /// <summary>Conexión no operativa o sin token: 422 <c>shopify.store.not_operational</c>.</summary>
    public static void EnsureOperational(ShopifyConnectionRef connection)
    {
        if (!connection.IsOperational || connection.AccessToken is null)
        {
            throw new ShopifyDomainException(ShopifyErrorCodes.StoreNotOperational, "The Shopify connection is not operational.");
        }
    }

    /// <summary>Sin <c>read_products</c> (ni <c>write_products</c>): 422 <c>scope_missing</c>, con la URL si vino
    /// <paramref name="redirectUri"/>.</summary>
    public static async Task EnsureReadProductsAsync(
        IShopifyReauthorization reauthorization, ShopifyConnectionRef connection, string? redirectUri, CancellationToken cancellationToken)
    {
        if (ShopifyScopes.CanReadProducts(connection.Scopes))
        {
            return;
        }

        var url = string.IsNullOrWhiteSpace(redirectUri)
            ? null
            : await reauthorization.CreateUrlAsync(connection.TenantId, connection.ShopDomain, redirectUri.Trim(), cancellationToken);
        throw new ShopifyProductsScopeMissingException(url);
    }
}
