namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Un dato que falta para facturar (spec 2026-11-18 §5.4, paso 2). <paramref name="Code"/> es contrato con la pantalla, que lo traduce:
/// <c>settings_incomplete</c> (<paramref name="Reference"/> = el paso), <c>variant_unlinked</c> (id de la variante, nombre),
/// <c>tax_unlinked</c> (porcentaje), <c>withholding_unlinked</c> y <c>withholding_type_unsupported</c> (id de la tasa, nombre),
/// <c>payment_unlinked</c> (clave de la cuenta de cobro; sin referencia cuando el pedido no tiene cuenta), <c>freight_product_missing</c>,
/// <c>customer_identification_missing</c>, <c>customer_identification_type_unsupported</c>, <c>customer_country_missing</c> (la
/// facturación no declara a qué país factura; ocupa el lugar del faltante de ciudad, porque sin país no se sabe qué ciudad pedir; sin
/// referencia ni etiqueta: el dato es del cliente; spec 2026-11-27), <c>customer_city_missing</c>,
/// <c>customer_city_unlinked</c> (la ciudad escrita fuera de Colombia no se vinculó a la lista de Siigo; sin referencia ni etiqueta: el
/// texto es del cliente; spec 2026-11-26), <c>currency_unsupported</c> (la moneda) y <c>seller_unlinked</c> (<paramref name="Reference"/> = la membresía del asesor y
/// <paramref name="Label"/> = su nombre o correo, que son de un miembro del tenant; spec 2026-11-20). Nunca lleva datos del cliente.
/// </summary>
public sealed record SiigoMissing(string Code, string? Reference, string? Label)
{
    public const string SettingsIncomplete = "settings_incomplete";
    public const string VariantUnlinked = "variant_unlinked";
    public const string TaxUnlinked = "tax_unlinked";
    public const string WithholdingUnlinked = "withholding_unlinked";
    public const string WithholdingTypeUnsupported = "withholding_type_unsupported";
    public const string PaymentUnlinked = "payment_unlinked";
    public const string FreightProductMissing = "freight_product_missing";
    public const string CustomerIdentificationMissing = "customer_identification_missing";
    public const string CustomerIdentificationTypeUnsupported = "customer_identification_type_unsupported";
    public const string CustomerCountryMissing = "customer_country_missing";
    public const string CustomerCityMissing = "customer_city_missing";
    public const string CustomerCityUnlinked = "customer_city_unlinked";
    public const string CurrencyUnsupported = "currency_unsupported";
    public const string SellerUnlinked = "seller_unlinked";
}
