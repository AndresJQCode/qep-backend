using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.ImporterWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §6.2.2 «Envío»: de las líneas de envío de Shopify al envío del pedido de QEP. El mapeo es puro: la
/// transportadora y la tasa del envío las busca el importador y entran ya resueltas (o <c>null</c>).
/// </summary>
public sealed class ShopifyShippingMapperTests
{
    private static readonly ShopifyCarrierRef Servientrega = new(Guid.Parse("01900000-0000-7000-8000-0000000000d1"), "Servientrega", true);
    private static readonly ShopifyCarrierRef NoCollect = new(Guid.Parse("01900000-0000-7000-8000-0000000000d2"), "Envía", false);

    private static readonly string?[] StoreChargeModes = ["Charged", "Free"];

    private static ShopifyShippingLine Line(decimal amount, string? title = "Servientrega", string? code = "standard", bool removed = false) =>
        new(code, title, amount, 0m, removed);

    // D-S28: con recogida no hay transportadora, ni flete, ni contraentrega, aunque la pasarela sea contraentrega.
    [Theory]
    [InlineData("pickup")]
    [InlineData("PICKUP-tienda-centro")]
    [InlineData(" pickup_point ")]
    public void PickupNoFreightNoCod(string code)
    {
        var snapshot = Order(
            gateways: ["Cash on Delivery (COD)"],
            shippingLines: [Line(5_000m, "Recogida en tienda", code)]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", Servientrega, 19);

        Assert.Equal(new ShopifyShippingDraft(true, null, null, null, 0m, 0, false), result.Draft);
        Assert.Equal(((string?)null, false, (string?)null), (result.UnrecognizedTitle, result.CashOnDeliveryUnmatched, result.WaitReason));
        Assert.True(ShopifyShippingMapper.IsStorePickup(snapshot));
    }

    // §6.2.2 paso 2: sin líneas de envío, envío sin registrar (ShippingDetails.None) y entrega a la dirección.
    [Fact]
    public void NoLinesNone()
    {
        var result = ShopifyShippingMapper.Map(Order(shippingLines: []), "COP", null, null);

        Assert.Equal(new ShopifyShippingDraft(false, null, null, null, 0m, 0, false), result.Draft);
        Assert.Equal(((string?)null, false, (string?)null), (result.UnrecognizedTitle, result.CashOnDeliveryUnmatched, result.WaitReason));
    }

    // Sólo cuentan las líneas no retiradas: el valor, el título y la recogida salen de ésas.
    [Fact]
    public void RemovedLineIgnored()
    {
        var snapshot = Order(shippingLines:
        [
            Line(20_000m, "DHL Express", removed: true),
            Line(0m, "Coordinadora"),
            Line(0m, "Recogida", "pickup", removed: true),
        ]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", null, 19);

        Assert.Equal(new ShopifyShippingDraft(false, null, null, "Free", 0m, 0, false), result.Draft);
        Assert.Equal("Coordinadora", result.UnrecognizedTitle);
        Assert.Equal("Coordinadora", ShopifyShippingMapper.CarrierTitle(snapshot));
        Assert.False(ShopifyShippingMapper.IsStorePickup(snapshot));
    }

    [Fact]
    public void OnlyRemovedLinesNone()
    {
        var result = ShopifyShippingMapper.Map(Order(shippingLines: [Line(20_000m, removed: true)]), "COP", Servientrega, 19);

        Assert.Equal(new ShopifyShippingDraft(false, null, null, null, 0m, 0, false), result.Draft);
        Assert.Null(result.UnrecognizedTitle);
    }

    // §6.2.2 paso 3: valor 0 (precio 0 o descuento de envío gratis) es Free, y no necesita la tasa del envío.
    [Fact]
    public void ZeroFree()
    {
        var result = ShopifyShippingMapper.Map(Order(shippingLines: [Line(0m)]), "COP", Servientrega, null);

        Assert.Equal(new ShopifyShippingDraft(false, Servientrega.Id, "Servientrega", "Free", 0m, 0, false), result.Draft);
        Assert.Null(result.WaitReason);
    }

    // Un valor negativo es un dato malo de Shopify, no un envío gratis. No se corrige ni se esconde como Free:
    // va como Charged con su valor y la regla del pedido lo rechaza (order.order.freight_amount_invalid, H21), así que la fila
    // queda order_rejected con ese código a la vista, como una línea con cantidad 0.
    [Theory]
    [InlineData(true, "-5000", "-5000")]
    [InlineData(false, "-10000", "-11900")]
    public void NegativeAmountIsNotFreeAndReachesTheOrderRules(bool taxesIncluded, string amount, string expected)
    {
        var snapshot = Order(taxesIncluded: taxesIncluded, shippingLines: [Line(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", Servientrega, 19);

        Assert.Equal(
            ("Charged", decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), (string?)null),
            (result.Draft.ChargeMode, result.Draft.FreightAmount, result.WaitReason));
    }

    // Con impuestos incluidos el valor va tal cual y la tasa del tenant viaja para que el pedido lo descomponga.
    [Fact]
    public void ChargedTaxesIncludedKeepsAmountWithTenantRate()
    {
        var result = ShopifyShippingMapper.Map(Order(taxesIncluded: true, shippingLines: [Line(19_000m)]), "COP", Servientrega, 19);

        Assert.Equal(new ShopifyShippingDraft(false, Servientrega.Id, "Servientrega", "Charged", 19_000m, 19, false), result.Draft);
        Assert.Null(result.WaitReason);
    }

    // Sin impuestos incluidos, en COP se le suma el IVA del envío del tenant (misma regla que los productos, D-S3).
    [Fact]
    public void ChargedTaxesExcludedGrossesUp()
    {
        var result = ShopifyShippingMapper.Map(Order(taxesIncluded: false, shippingLines: [Line(10_000m)]), "COP", Servientrega, 19);

        Assert.Equal(("Charged", 11_900m, 19), (result.Draft.ChargeMode, result.Draft.FreightAmount, result.Draft.FreightTaxPercentage));
    }

    // H21: el dominio exige pesos enteros en COP; se redondea AwayFromZero en vez de dejar que el pedido lance
    // freight_amount_invalid.
    [Theory]
    [InlineData(true, "15000.50", "15001")]
    [InlineData(true, "15000.49", "15000")]
    [InlineData(false, "19999.50", "23799")]
    public void ShippingCopChargedWithCentsRoundsToWholePesos(bool taxesIncluded, string amount, string expected)
    {
        var snapshot = Order(taxesIncluded: taxesIncluded, shippingLines: [Line(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", Servientrega, 19);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result.Draft.FreightAmount);
        Assert.Equal(result.Draft.FreightAmount, decimal.Truncate(result.Draft.FreightAmount));
    }

    // §6.2.2 paso 4 (H22): un Charged en COP necesita la tasa del envío del tenant.
    [Fact]
    public void ChargedCopWithoutRateWaitsFreightTaxRateMissing()
    {
        var snapshot = Order(shippingLines: [Line(15_000m, "Envío")]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", null, null);

        Assert.Equal(ShopifyWaitReasons.FreightTaxRateMissing, result.WaitReason);
        Assert.Equal(("Envío", 15_000m), (ShopifyShippingMapper.CarrierTitle(snapshot), ShopifyShippingMapper.Amount(snapshot)));
    }

    // D-S27: fuera de COP el valor va tal cual, la tasa se fuerza a 0 y no hace falta la del tenant.
    [Fact]
    public void ChargedUsdKeepsAmountWithoutRate()
    {
        var result = ShopifyShippingMapper.Map(Order(currency: "USD", taxesIncluded: false, shippingLines: [Line(12.55m)]), "USD", null, null);

        Assert.Equal(new ShopifyShippingDraft(false, null, null, "Charged", 12.55m, 0, false), result.Draft);
        Assert.Null(result.WaitReason);
    }

    // D-S30: con varias líneas no retiradas se suman los valores y la transportadora sale del título de la primera.
    [Fact]
    public void TwoLinesSumsAndUsesFirstTitle()
    {
        var snapshot = Order(shippingLines: [Line(10_000m, "Servientrega"), Line(5_000m, "Coordinadora")]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", null, 19);

        Assert.Equal(15_000m, result.Draft.FreightAmount);
        Assert.Equal(("Servientrega", "Servientrega"), (ShopifyShippingMapper.CarrierTitle(snapshot), result.UnrecognizedTitle));
    }

    // §6.2.2 paso 5: sin coincidencia el pedido entra sin transportadora y con el título para «reconocer este nombre».
    [Fact]
    public void UnknownTitleWarnsCarrierUnrecognized()
    {
        var snapshot = Order(shippingLines: [Line(19_000m, "  Inter Rapidísimo ")]);

        var unknown = ShopifyShippingMapper.Map(snapshot, "COP", null, 19);
        var known = ShopifyShippingMapper.Map(snapshot, "COP", Servientrega, 19);

        Assert.Equal(((Guid?)null, (string?)null, "Inter Rapidísimo"), (unknown.Draft.CarrierId, unknown.Draft.CarrierName, unknown.UnrecognizedTitle));
        Assert.Equal(((Guid?)Servientrega.Id, "Servientrega", (string?)null), (known.Draft.CarrierId, known.Draft.CarrierName, known.UnrecognizedTitle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTitleNoWarning(string? title)
    {
        var snapshot = Order(shippingLines: [Line(19_000m, title)]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", null, 19);

        Assert.Null(result.UnrecognizedTitle);
        Assert.Null(ShopifyShippingMapper.CarrierTitle(snapshot));
    }

    // §6.2.2 paso 6: la contraentrega es un hecho de la tienda, con o sin transportadora reconocida.
    [Fact]
    public void CodGatewaySetsCashOnDelivery()
    {
        var snapshot = Order(gateways: ["Mercado Pago", "Cash on Delivery (COD)"], shippingLines: [Line(19_000m)]);

        var withCarrier = ShopifyShippingMapper.Map(snapshot, "COP", Servientrega, 19);
        var withoutCarrier = ShopifyShippingMapper.Map(snapshot, "COP", null, 19);

        Assert.Equal((true, false), (withCarrier.Draft.CashOnDelivery, withCarrier.CashOnDeliveryUnmatched));
        Assert.Equal((true, false), (withoutCarrier.Draft.CashOnDelivery, withoutCarrier.CashOnDeliveryUnmatched));
    }

    [Fact]
    public void CodWithNonCollectingCarrierWarnsUnmatched()
    {
        var snapshot = Order(gateways: ["Pago contra entrega"], shippingLines: [Line(19_000m, "Envía")]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", NoCollect, 19);

        Assert.Equal((true, true, (Guid?)NoCollect.Id), (result.Draft.CashOnDelivery, result.CashOnDeliveryUnmatched, result.Draft.CarrierId));
    }

    [Fact]
    public void NonCollectingCarrierWithoutCodDoesNotWarn()
    {
        var result = ShopifyShippingMapper.Map(Order(gateways: ["Mercado Pago"], shippingLines: [Line(19_000m)]), "COP", NoCollect, 19);

        Assert.Equal((false, false), (result.Draft.CashOnDelivery, result.CashOnDeliveryUnmatched));
    }

    // «Al cobro» (OnDelivery) nunca sale de la tienda: Shopify no lo conoce.
    [Theory]
    [InlineData("0", true)]
    [InlineData("19000", true)]
    [InlineData("19000", false)]
    public void NeverOnDelivery(string amount, bool cod)
    {
        var snapshot = Order(
            gateways: cod ? ["contraentrega"] : ["manual"],
            shippingLines: [Line(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))]);

        var result = ShopifyShippingMapper.Map(snapshot, "COP", Servientrega, 19);

        Assert.Contains(result.Draft.ChargeMode, StoreChargeModes);
    }

    // V-8: los cuatro valores iniciales, normalizados como los nombres de transportadora (sin mayúsculas, tildes ni espacios
    // repetidos).
    [Theory]
    [InlineData("Cash on Delivery (COD)", true)]
    [InlineData("  CASH   on delivery (cod) ", true)]
    [InlineData("Contra Entrega", true)]
    [InlineData("Contraentrega", true)]
    [InlineData("Pago Contra Entrega", true)]
    [InlineData("pago contra éntrega", true)]
    [InlineData("Mercado Pago", false)]
    [InlineData("contra-entrega", false)]
    [InlineData("", false)]
    public void CashOnDeliveryGatewaysMatchNormalized(string gateway, bool expected)
    {
        Assert.Equal(expected, ShopifyCashOnDeliveryGateways.Matches([gateway]));
    }

    [Fact]
    public void CashOnDeliveryGatewaysAreTheFourOfTheSpec()
    {
        Assert.Equal(["cash on delivery (cod)", "contra entrega", "contraentrega", "pago contra entrega"], ShopifyCashOnDeliveryGateways.Values);
        Assert.False(ShopifyCashOnDeliveryGateways.Matches([]));
    }
}
