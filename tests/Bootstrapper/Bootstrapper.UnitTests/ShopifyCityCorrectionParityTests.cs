using Modules.Quotations.Domain;
using Modules.Shopify.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-26 D-C4: la huella de <c>shopify.city_corrections</c> guarda la parte de envío tal como la guardó Quotations, así
/// que sus columnas tienen los anchos de <see cref="QuotationPartyDetails"/>. Shopify no ve Quotations (ShopifyLayerTests): los
/// anchos están copiados y esta prueba, que ve los dos módulos, los amarra. Si uno se ensancha solo, la huella de una parte larga
/// no cabe y la importación falla con 22001.
/// </summary>
public sealed class ShopifyCityCorrectionParityTests
{
    [Fact]
    public void PartyWidthsMatchTheOrderParty() =>
        Assert.Equal(
            (QuotationPartyDetails.NameMaxLength, QuotationPartyDetails.PhoneMaxLength, QuotationPartyDetails.EmailMaxLength,
                QuotationPartyDetails.AddressMaxLength),
            (ShopifyCityCorrection.PartyNameMaxLength, ShopifyCityCorrection.PartyPhoneMaxLength, ShopifyCityCorrection.PartyEmailMaxLength,
                ShopifyCityCorrection.PartyAddressMaxLength));
}
