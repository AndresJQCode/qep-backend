using Modules.Quotations.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Ruling S22: Shopify no referencia Quotations, así que <see cref="ShopifyCashOnDeliveryGateways.Normalize"/> es una copia de
/// <see cref="CarrierName.Normalize"/> (spec 2026-10-15 §6.2.2 paso 6: las pasarelas se normalizan «igual que los nombres de
/// transportadora»). Este proyecto ve los dos módulos: si Quotations cambia el algoritmo, esta prueba se rompe en vez de que la
/// contraentrega diverja en silencio.
/// </summary>
public sealed class ShopifyNormalizationParityTests
{
    public static TheoryData<string> Inputs() =>
    [
        "",
        " ",
        "\t\n ",
        "Cash on Delivery (COD)",
        "  CASH   on\tdelivery (cod) ",
        "Pago Contra Entrega",
        "pago contra éntrega",
        "ÁÉÍÓÚ áéíóú Ññ Üü",
        "Envía",
        "Inter Rapidísimo",
        "contra-entrega",
        "Contra_Entrega!",
        "Coordinadora S.A.S.",
        "Ç ç Ø ß",
        "é",
        "İstanbul",
        "a b",
        "50% · Envío #1",
    ];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void CashOnDeliveryGatewaysNormalizeLikeCarrierNames(string input) =>
        Assert.Equal(CarrierName.Normalize(input), ShopifyCashOnDeliveryGateways.Normalize(input));
}
