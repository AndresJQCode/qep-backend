using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La compra mínima que habilita cualquier descuento de escala: **N unidades o un total mínimo en
/// la moneda de la cotización**, en la cotización entera (decisión del owner, 2026-09-21). Es un
/// OR: con las unidades la plata ni se mira, y con plata suficiente la cantidad ni se mira.
///
/// <b>Se mide sobre la cotización completa</b>, no por grupo de escala: dos líneas de productos
/// distintos suman para alcanzarla igual que suman para alcanzar un tramo.
///
/// <b>Se mide sobre el total ya descontado</b> (<see cref="Quotation.Total"/>, que lleva el IVA
/// adentro porque los precios del catálogo ya lo traen), y en **una sola pasada**: se calcula el
/// descuento, se mira el total resultante, y si no llega se quita el descuento y se termina. No
/// se vuelve a evaluar. Sin ese corte la regla es circular —quitar el descuento sube el total, que
/// entonces sí alcanzaría el mínimo, que devuelve el descuento, que vuelve a bajarlo— y no
/// converge.
///
/// <b>El precio de ese corte, aceptado explícitamente por el owner:</b> una cotización por debajo
/// de las unidades mínimas (6 con los valores por defecto) cuyo bruto de lista caiga entre el
/// mínimo y <c>mínimo / (1 − descuento)</c> termina sin descuento y con un total **por encima** del
/// mínimo que la pantalla dice no haber alcanzado. Con 5% de descuento y los valores por defecto
/// en COP eso es el rango $500.000–$526.316. Si algún día hay que cerrar
/// ese hueco, la salida es medir la plata sobre el bruto de lista, no iterar hasta un punto fijo.
///
/// Per-currency threshold, configurable per tenant (spec 2026-10-08, D7); a currency without a
/// threshold only passes by units. See <see cref="QuotationSettings"/>.
/// </summary>
internal static class QuotationMinimumPurchase
{
    /// <summary>
    /// Whether the quotation, **as it stands with the discounts already applied**, enables the
    /// discount. Called after applying them, never before: the total that decides is the one after.
    /// </summary>
    public static bool IsSatisfiedBy(Quotation quotation, QuotationSettings settings) =>
        IsMet(quotation.Items.Sum(item => item.Quantity), quotation.Total, quotation.Currency, settings);

    /// <summary>
    /// The state the frontend paints. Built from units, total and currency only, so it reads the
    /// same in a recalculation and on a stored quotation. <c>MinimumTotal</c>/<c>MissingTotal</c> are
    /// null when the currency has no configured total: the screen says "only by units".
    /// </summary>
    public static QuotationMinimumPurchaseDto DescribeFor(
        decimal units, decimal total, string currency, QuotationSettings settings)
    {
        var minimumTotal = settings.MinimumTotalFor(currency);
        var met = IsMet(units, total, currency, settings);

        return new QuotationMinimumPurchaseDto(
            met,
            units,
            settings.MinimumUnits,
            currency,
            minimumTotal,
            // It is an OR: once met, nothing is missing on either branch.
            met ? 0m : settings.MinimumUnits - units,
            minimumTotal is null ? null : met ? 0m : minimumTotal.Value - total);
    }

    private static bool IsMet(decimal units, decimal total, string currency, QuotationSettings settings) =>
        units >= settings.MinimumUnits
        || (settings.MinimumTotalFor(currency) is { } minimum && total >= minimum);
}
