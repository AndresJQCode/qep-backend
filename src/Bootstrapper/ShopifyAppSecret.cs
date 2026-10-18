using Modules.Integrations.Application.Shopify;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §5.1: el client secret de la app de Integrations (tarea 5) para la firma del webhook de Shopify. Shopify
/// no referencia Integrations (ShopifyLayerTests): el composition root los une.</summary>
internal sealed class ShopifyAppSecret(IShopifyAppSettings settings) : IShopifyAppSecret
{
    public string? ClientSecret => settings.ClientSecret;
}
