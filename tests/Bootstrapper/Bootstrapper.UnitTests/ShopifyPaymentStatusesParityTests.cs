using Modules.Quotations.Domain;
using Modules.Shopify.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Shopify no referencia Quotations, así que <see cref="ShopifyPaymentStatuses.All"/> es una copia de los nombres de
/// <see cref="OrderPaymentStatus"/> (spec 2026-11-14: con qué estados de pago la tienda admite un cambio de Shopify). Este proyecto
/// ve los dos módulos: si Quotations suma, quita o renombra un estado, esta prueba se rompe en vez de que la configuración de la
/// tienda guarde un nombre que el pedido ya no tiene.
/// </summary>
public sealed class ShopifyPaymentStatusesParityTests
{
    [Fact]
    public void TheNamesMatchOrderPaymentStatus() =>
        Assert.Equal(
            Enum.GetNames<OrderPaymentStatus>().Order(StringComparer.Ordinal),
            ShopifyPaymentStatuses.All.Order(StringComparer.Ordinal));
}
