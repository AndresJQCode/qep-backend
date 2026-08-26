namespace Modules.Quotations.Domain;

/// <summary>
/// Sobrescrituras de facturación/entrega para una sola cotización (modelo-datos-cotizaciones.md
/// §1.8/§2.1, US-6). Null = usa el dato del cliente maestro; guardar estos campos nunca actualiza
/// el registro del cliente. Agrupados por el mismo criterio que <c>ProductDetails</c> y
/// <c>CustomerContactInfo</c>: van juntos porque conceptualmente son un solo "paquete" que se
/// reemplaza entero.
/// </summary>
public sealed record QuotationOverrides
{
    public string? BillingName { get; init; }

    public string? BillingAddress { get; init; }

    public string? DeliveryAddress { get; init; }

    public string? DeliveryCity { get; init; }

    public const int BillingNameMaxLength = 255;

    public const int BillingAddressMaxLength = 255;

    public const int DeliveryAddressMaxLength = 255;

    public const int DeliveryCityMaxLength = 100;

    public static QuotationOverrides Empty { get; } = new();

    internal QuotationOverrides Normalized() => new()
    {
        BillingName = NormalizeOptional(
            BillingName,
            BillingNameMaxLength,
            "quotation.quotation.billing_name_override_too_long",
            $"The billing name override cannot exceed {BillingNameMaxLength} characters."),
        BillingAddress = NormalizeOptional(
            BillingAddress,
            BillingAddressMaxLength,
            "quotation.quotation.billing_address_override_too_long",
            $"The billing address override cannot exceed {BillingAddressMaxLength} characters."),
        DeliveryAddress = NormalizeOptional(
            DeliveryAddress,
            DeliveryAddressMaxLength,
            "quotation.quotation.delivery_address_override_too_long",
            $"The delivery address override cannot exceed {DeliveryAddressMaxLength} characters."),
        DeliveryCity = NormalizeOptional(
            DeliveryCity,
            DeliveryCityMaxLength,
            "quotation.quotation.delivery_city_override_too_long",
            $"The delivery city override cannot exceed {DeliveryCityMaxLength} characters.")
    };

    // Vacio y ausente son lo mismo para un campo opcional, mismo criterio que
    // CustomerContactInfo.NormalizeOptional.
    private static string? NormalizeOptional(
        string? value, int maxLength, string tooLongCode, string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new QuotationsDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }
}
