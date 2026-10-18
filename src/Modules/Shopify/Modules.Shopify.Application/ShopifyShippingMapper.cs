using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>
/// Lo que el mapeo del envío le devuelve al importador. <see cref="Draft"/> es el envío del borrador del pedido;
/// <see cref="UnrecognizedTitle"/> es el título que no coincidió con ninguna transportadora (aviso <c>carrier_unrecognized</c> y
/// <c>ExternalShippingTitle</c> del pedido, §6.2.5); <see cref="CashOnDeliveryUnmatched"/> marca una contraentrega con una
/// transportadora que no recauda (aviso <c>cash_on_delivery_unmatched</c>); <see cref="WaitReason"/> es
/// <c>freight_tax_rate_missing</c> cuando el pedido no puede entrar todavía.
/// </summary>
public sealed record ShopifyShippingResult(
    ShopifyShippingDraft Draft, string? UnrecognizedTitle, bool CashOnDeliveryUnmatched, string? WaitReason);

/// <summary>
/// Spec 2026-10-15 §6.2.2 «Envío» (delta del flete §10): de las líneas de envío de Shopify al envío del pedido. Es puro: la
/// transportadora (<c>IShopifyShippingLookup.MatchCarrierAsync</c> con <see cref="CarrierTitle"/>) y la tasa del envío del
/// tenant las busca el importador y entran resueltas, o <c>null</c>.
/// <list type="number">
/// <item>Sólo cuentan las líneas no retiradas.</item>
/// <item>Recogida (un <c>code</c> que empieza por <c>pickup</c>, V-6): sin transportadora, sin flete y sin contraentrega
/// (D-S28); el dominio rechazaría cualquier envío con recogida.</item>
/// <item>Sin líneas: envío sin registrar.</item>
/// <item>Valor = Σ <c>discounted_price</c>. 0 es <c>Free</c>; si no, <c>Charged</c> con el IVA del envío como los productos
/// (D-S3, D-S27) y, en COP, en pesos enteros <c>AwayFromZero</c> (H21). Un <c>Charged</c> en COP sin tasa espera
/// <c>freight_tax_rate_missing</c>. «Al cobro» nunca sale de la tienda.</item>
/// <item>Transportadora del título de la primera línea (D-S30); la contraentrega, de las pasarelas, con o sin transportadora.</item>
/// </list>
/// </summary>
public static class ShopifyShippingMapper
{
    public const string Charged = "Charged";
    public const string Free = "Free";

    private const string PickupCodePrefix = "pickup";

    private static readonly ShopifyShippingDraft NoShipping = new(false, null, null, null, 0m, 0, false);
    private static readonly ShopifyShippingDraft StorePickup = new(true, null, null, null, 0m, 0, false);

    public static ShopifyShippingResult Map(ShopifyOrderSnapshot snapshot, string currency, ShopifyCarrierRef? carrier, int? freightRate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsStorePickup(snapshot))
        {
            return new ShopifyShippingResult(StorePickup, null, false, null);
        }

        if (!ActiveLines(snapshot).Any())
        {
            return new ShopifyShippingResult(NoShipping, null, false, null);
        }

        var amount = Amount(snapshot);
        var isCop = ShopifyPricing.IsCop(currency);
        string chargeMode;
        decimal freightAmount;
        int taxPercentage;
        if (amount <= 0m)
        {
            (chargeMode, freightAmount, taxPercentage) = (Free, 0m, 0);
        }
        else if (!isCop)
        {
            // D-S27: fuera de COP el valor va tal cual y la tasa se fuerza a 0 en el pedido.
            (chargeMode, freightAmount, taxPercentage) = (Charged, amount, 0);
        }
        else if (freightRate is not { } rate)
        {
            return new ShopifyShippingResult(NoShipping, null, false, ShopifyWaitReasons.FreightTaxRateMissing);
        }
        else
        {
            var withVat = ShopifyPricing.UnitPriceWithVat(amount, snapshot.TaxesIncluded, rate, currency);
            (chargeMode, freightAmount, taxPercentage) = (Charged, Math.Round(withVat, 0, MidpointRounding.AwayFromZero), rate);
        }

        var cashOnDelivery = ShopifyCashOnDeliveryGateways.Matches(snapshot.PaymentGateways);
        var title = CarrierTitle(snapshot);
        var draft = new ShopifyShippingDraft(
            false, carrier?.Id, carrier?.Name, chargeMode, freightAmount, taxPercentage, cashOnDelivery);
        return new ShopifyShippingResult(
            draft,
            carrier is null ? title : null,
            cashOnDelivery && carrier is { SupportsCashOnDelivery: false },
            null);
    }

    /// <summary>§6.2.2 paso 1 (V-6): alguna línea no retirada con un <c>code</c> que empieza por <c>pickup</c>.</summary>
    public static bool IsStorePickup(ShopifyOrderSnapshot snapshot) =>
        ActiveLines(snapshot).Any(line =>
            line.Code?.Trim().StartsWith(PickupCodePrefix, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>D-S30: el título recortado de la primera línea no retirada, o <c>null</c> si viene vacío o no hay líneas.</summary>
    public static string? CarrierTitle(ShopifyOrderSnapshot snapshot)
    {
        var title = ActiveLines(snapshot).FirstOrDefault()?.Title?.Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    /// <summary>§6.2.2 paso 3: Σ <c>discounted_price</c> de las líneas no retiradas, tal como lo cobró Shopify.</summary>
    public static decimal Amount(ShopifyOrderSnapshot snapshot) => ActiveLines(snapshot).Sum(line => line.DiscountedPrice);

    private static IEnumerable<ShopifyShippingLine> ActiveLines(ShopifyOrderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ShippingLines.Where(line => line is { IsRemoved: false });
    }
}
