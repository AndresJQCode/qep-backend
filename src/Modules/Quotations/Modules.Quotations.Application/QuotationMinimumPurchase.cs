using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La compra mínima que habilita cualquier descuento de escala: **$500.000 COP o 200 USD o 6
/// unidades**, en la cotización entera (decisión del owner, 2026-09-21). Es un OR: con 6 unidades
/// la plata ni se mira, y con plata suficiente la cantidad ni se mira.
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
/// <b>El precio de ese corte, aceptado explícitamente por el owner:</b> una cotización de menos de
/// 6 unidades cuyo bruto de lista caiga entre el mínimo y <c>mínimo / (1 − descuento)</c> termina
/// sin descuento y con un total **por encima** del mínimo que la pantalla dice no haber
/// alcanzado. Con 5% de descuento eso es el rango $500.000–$526.316. Si algún día hay que cerrar
/// ese hueco, la salida es medir la plata sobre el bruto de lista, no iterar hasta un punto fijo.
///
/// <b>Umbral por moneda, sin conversión.</b> Este módulo no tiene tabla de cambio y lo rechaza por
/// decisión explícita (<see cref="QuotationCurrency"/>), así que el mínimo en dólares es un número
/// propio y no una conversión del de pesos. Un literal en pesos dejaría la rama de plata muerta
/// para toda cotización en USD, que nunca llegaría a 500.000.
/// </summary>
internal static class QuotationMinimumPurchase
{
    public const decimal MinimumUnits = 6m;

    public const decimal MinimumTotalCop = 500_000m;

    public const decimal MinimumTotalUsd = 200m;

    /// <summary>El mínimo en plata que le corresponde a una moneda. Ver la nota sobre por qué no
    /// es una conversión.</summary>
    public static decimal MinimumTotalFor(QuotationCurrency currency) =>
        currency == QuotationCurrency.Usd ? MinimumTotalUsd : MinimumTotalCop;

    /// <summary>
    /// Si la cotización, **tal como quedó con los descuentos ya aplicados**, habilita el descuento.
    /// Se llama después de aplicarlos, nunca antes: el total que decide es el de después.
    /// </summary>
    public static bool IsSatisfiedBy(Quotation quotation) =>
        quotation.Items.Sum(item => item.Quantity) >= MinimumUnits
        || quotation.Total >= MinimumTotalFor(quotation.Currency);

    /// <summary>
    /// El estado de la compra mínima tal como viaja al frontend. Se arma desde la cotización y
    /// nada más —unidades, total y moneda—, así que sale igual de bien en un recálculo que al leer
    /// una cotización guardada, sin tocar el catálogo. Ver
    /// <see cref="QuotationMinimumPurchaseDto"/> para por qué el mensaje es prospectivo.
    /// </summary>
    public static QuotationMinimumPurchaseDto DescribeFor(Quotation quotation)
    {
        var units = quotation.Items.Sum(item => item.Quantity);
        var minimumTotal = MinimumTotalFor(quotation.Currency);
        var met = units >= MinimumUnits || quotation.Total >= minimumTotal;

        return new QuotationMinimumPurchaseDto(
            met,
            units,
            MinimumUnits,
            minimumTotal,
            // Con el mínimo alcanzado los dos faltantes son 0, aunque una de las ramas siga corta:
            // es un OR, y decir "te faltan 4 unidades" en una cotización que ya tiene el descuento
            // sería pedirle al cliente algo que no necesita.
            met ? 0m : MinimumUnits - units,
            met ? 0m : minimumTotal - quotation.Total);
    }
}
