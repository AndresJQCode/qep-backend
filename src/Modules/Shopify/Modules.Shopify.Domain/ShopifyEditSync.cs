namespace Modules.Shopify.Domain;

/// <summary>
/// Spec 2026-11-14: qué hace la tienda con una edición de Shopify sobre un pedido ya importado. <see cref="Manual"/> deja el
/// cambio esperando a que el asesor lo acepte; <see cref="Automatic"/> lo aplica solo cuando se puede. Viaja y se guarda por
/// nombre.
/// </summary>
public enum ShopifyEditSync
{
    Manual,
    Automatic,
}
