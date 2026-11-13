namespace Modules.Integrations.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-12 §6.2 (D1): el espacio del candado de base de datos que serializa el refresco y el reemplazo del token de
/// Shopify de una conexión o de una instalación. Va en la forma de dos claves, <c>(Namespace, hashtext(id))</c>, que es un
/// espacio aparte del de una clave; el número no lo usa ningún otro candado de dos claves (<c>TenantChangeLock</c>,
/// <c>AutomationsConversationLock</c>). Sin él, dos refrescos de la misma tienda consumirían dos veces el refresh token, y
/// Shopify puede dejar guardado un par ya retirado.
/// </summary>
internal static class ShopifyTokenLock
{
    public const int Namespace = 20261112;
}
