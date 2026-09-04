namespace Modules.Reporting.Domain;

/// <summary>
/// El estado de pago por el que se puede filtrar el reporte de ventas. Son exactamente los tres
/// valores de <c>SalePaymentStatus</c> en Quotations, redeclarados acá y no referenciados: el
/// dominio de un módulo de negocio no referencia el dominio de otro — mismo criterio que
/// <c>Quotations.MemberId</c> frente al <c>MembershipId</c> de Tenancy.
/// </summary>
public enum SalePaymentStatusFilter
{
    FullPaymentReceived,
    PartialPaymentReceived,
    PaymentPending
}

/// <summary>
/// Los cuatro estados de <c>QuotationStatus</c>. Ver <see cref="SalePaymentStatusFilter"/> sobre
/// por qué se redeclaran.
///
/// **No hay "Approved"**: convertir una cotización en venta la deja en <see cref="Sent"/>, y la
/// única señal de que se convirtió es que exista una <c>Sale</c> apuntándola 1:1. Para "las
/// cotizaciones que terminaron en venta" el reporte a usar es el de ventas, no un filtro de
/// estado acá.
/// </summary>
public enum QuotationStatusFilter
{
    Draft,
    Sent,
    Expired,
    Voided
}

/// <summary>Los tres valores de <c>ProductPriceField</c> en Catalog. Ver
/// <see cref="SalePaymentStatusFilter"/> sobre por qué se redeclaran.</summary>
public enum PriceChangeField
{
    PriceBaseUsd,
    PriceBaseCop,
    ScaleDiscount
}

/// <summary>
/// Traduce el texto que llega por query string al enum de filtro.
///
/// **Un valor que no se reconoce falla, no cae en un default**: elegir un estado en silencio le
/// cambia el reporte a quien lo pidió sin que se entere. Mismo criterio que
/// <c>IdentificationTypeParser</c> en Customers.
///
/// La comparación es exacta y sensible a mayúsculas: los valores del contrato viajan en
/// PascalCase (<c>FullPaymentReceived</c>), que es como los serializa el resto de la API.
/// </summary>
public static class ReportFilterParser
{
    public static bool TryParsePaymentStatus(string? value, out SalePaymentStatusFilter parsed) =>
        TryParse(value, out parsed);

    public static bool TryParseQuotationStatus(string? value, out QuotationStatusFilter parsed) =>
        TryParse(value, out parsed);

    public static bool TryParsePriceChangeField(string? value, out PriceChangeField parsed) =>
        TryParse(value, out parsed);

    /// <summary>El valor parseado, o <c>null</c> si no vino ninguno. Un texto presente pero
    /// inválido tira <see cref="ReportingDomainException"/>: el validador de FluentValidation lo
    /// atrapa antes con el nombre del campo, y esto es la red de abajo.</summary>
    public static SalePaymentStatusFilter? ParsePaymentStatus(string? value) =>
        Parse<SalePaymentStatusFilter>(value, "paymentStatus");

    /// <summary>Ver <see cref="ParsePaymentStatus"/>.</summary>
    public static QuotationStatusFilter? ParseQuotationStatus(string? value) =>
        Parse<QuotationStatusFilter>(value, "status");

    /// <summary>Ver <see cref="ParsePaymentStatus"/>.</summary>
    public static PriceChangeField? ParsePriceChangeField(string? value) =>
        Parse<PriceChangeField>(value, "field");

    private static bool TryParse<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed) && Enum.IsDefined(parsed);

    private static TEnum? Parse<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return TryParse<TEnum>(value, out var parsed)
            ? parsed
            : throw new ReportingDomainException(
                "reporting.filter.invalid",
                $"The '{field}' filter is not one of the supported values.");
    }
}
