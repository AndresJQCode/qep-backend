using Modules.Integrations.Application.Shopify;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §5.3 y D-S29: la versión de la Admin API de la conciliación es la misma <c>Shopify:App:ApiVersion</c>
/// que Integrations usa para el enlace (con su default y su validación): un solo lugar. Shopify no referencia Integrations
/// (ShopifyLayerTests): el composition root los une.</summary>
internal sealed class ShopifyApiVersion(IShopifyAppSettings settings) : IShopifyApiVersion
{
    public string Value => settings.ApiVersion;
}
