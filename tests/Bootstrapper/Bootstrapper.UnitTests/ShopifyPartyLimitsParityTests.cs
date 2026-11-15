using Modules.Quotations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-14 §5: el validador de la corrección de un pedido de Shopify (<c>PUT …/corrections</c>) usa los topes de la parte
/// de envío de Quotations, copiados en <see cref="ShopifyPartyLimits"/> porque Shopify no ve Quotations (ShopifyLayerTests). Si el
/// dominio se ensancha o se angosta solo, un valor fuera de rango sale como 422 de dominio sin el mapa <c>errors</c>, que es el único
/// que el formulario sabe leer.
/// </summary>
public sealed class ShopifyPartyLimitsParityTests
{
    [Fact]
    public void TheLimitsMatchQuotationPartyDetails() =>
        Assert.Equal(
            (QuotationPartyDetails.NameMaxLength, QuotationPartyDetails.PhoneMaxLength, QuotationPartyDetails.EmailMaxLength,
                QuotationPartyDetails.AddressMaxLength),
            (ShopifyPartyLimits.NameMaxLength, ShopifyPartyLimits.PhoneMaxLength, ShopifyPartyLimits.EmailMaxLength,
                ShopifyPartyLimits.AddressMaxLength));
}
