namespace Modules.Quotations.Domain;

/// <summary>
/// La moneda en la que está expresada una cotización entera: sus precios unitarios, sus
/// descuentos, sus impuestos y sus totales.
///
/// Son dos y no un código libre porque son las dos que el catálogo sabe cotizar:
/// <c>Product.PriceBaseCop</c> y <c>Product.PriceBaseUsd</c>. No hay tabla de cambio en ningún
/// lado, así que una tercera moneda no se podría convertir — se rechaza, no se aproxima.
/// </summary>
public enum QuotationCurrency
{
    Cop,
    Usd
}

public static class QuotationCurrencies
{
    public const string CopCode = "COP";

    public const string UsdCode = "USD";

    /// <summary>
    /// La moneda de una cotización que todavía no eligió cuenta de cobro. Es COP por herencia:
    /// hasta que existió la cuenta de facturación, US-5 decía que todo valor monetario del módulo
    /// era COP, y las cotizaciones que ya existen quedaron así.
    /// </summary>
    public const QuotationCurrency Default = QuotationCurrency.Cop;

    public static string ToCode(this QuotationCurrency currency) =>
        currency == QuotationCurrency.Usd ? UsdCode : CopCode;

    /// <summary>
    /// La moneda que corresponde al código ISO de una cuenta bancaria de la empresa.
    ///
    /// Una cuenta en cualquier otra moneda no se puede usar para facturar: el catálogo no tiene
    /// precio en esa moneda y este módulo no convierte. Falla como código de dominio —422 con
    /// mensaje— y no como una conversión inventada.
    /// </summary>
    public static QuotationCurrency FromCode(string code) => code?.Trim().ToUpperInvariant() switch
    {
        CopCode => QuotationCurrency.Cop,
        UsdCode => QuotationCurrency.Usd,
        _ => throw new QuotationsDomainException(
            "quotation.billing.currency_unsupported",
            $"Quotations can only be issued in {CopCode} or {UsdCode}.")
    };
}

/// <summary>
/// El precio de una línea en una moneda dada, ya resuelto por la aplicación contra el catálogo
/// (precio base, escala de cantidad y tasa de impuesto del producto).
///
/// Existe para revalorizar: cuando la cotización cambia de moneda, cada línea guardada tiene que
/// volver a nacer con el precio del producto en la moneda nueva. El dominio no sabe consultar el
/// catálogo — recibe el resultado y lo aplica, mismo criterio que <c>AddItem</c>.
/// </summary>
public sealed record QuotationItemPricing(
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage);
