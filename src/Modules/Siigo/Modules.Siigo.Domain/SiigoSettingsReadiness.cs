namespace Modules.Siigo.Domain;

/// <summary>Los seis pasos del asistente y de la pantalla de configuración (spec 2026-11-18 §5.3), en el orden en que se recorren.</summary>
public enum SiigoSetupStep
{
    Credentials,
    Company,
    Document,
    Taxes,
    Payments,
    Sellers,
    Products,
}

/// <summary>Un paso: completo, o incompleto con los códigos de lo que falta (<c>snake_case</c>, los traduce la pantalla).</summary>
public sealed record SiigoStepReadiness(SiigoSetupStep Step, bool Complete, IReadOnlyList<string> MissingCodes);

/// <summary>
/// Spec 2026-11-18 §5.3: el <c>readiness</c> por paso que devuelve <c>GET settings</c>. Sólo mira lo que la fila sabe de sí misma:
/// <c>Credentials</c> lo decide la conexión de Integrations (el handler lo ajusta) y <c>Products</c> informa la cobertura sin bloquear.
/// Impuestos y formas de pago no tienen un mínimo por pedido: qué vínculo hace falta depende del pedido, y cada faltante sale al generar la
/// factura (<c>siigo.invoice.tax_unlinked</c>, <c>payment_unlinked</c>…), con el pedido a la vista. Pero un paso sin <b>ningún</b> vínculo
/// no está configurado, así que no se marca completo hasta que haya al menos uno (con un impuesto o forma de pago de Siigo elegido).
/// </summary>
public static class SiigoSettingsReadiness
{
    public const string CompanyMissing = "company_missing";
    public const string DocumentTypeMissing = "document_type_missing";
    public const string TaxLinkMissing = "tax_link_missing";
    public const string PaymentLinkMissing = "payment_link_missing";
    public const string SellerLinkMissing = "seller_link_missing";

    public static IReadOnlyList<SiigoStepReadiness> Evaluate(SiigoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return
        [
            Of(SiigoSetupStep.Credentials),
            Of(SiigoSetupStep.Company, settings.CompanyId is null ? CompanyMissing : null),
            Of(SiigoSetupStep.Document, settings.DocumentTypeId is null ? DocumentTypeMissing : null),
            Of(SiigoSetupStep.Taxes, HasTaxLink(settings) ? null : TaxLinkMissing),
            Of(SiigoSetupStep.Payments, HasPaymentLink(settings) ? null : PaymentLinkMissing),
            Of(SiigoSetupStep.Sellers, settings.SellerLinks.Count > 0 ? null : SellerLinkMissing),
            Of(SiigoSetupStep.Products),
        ];
    }

    // Un paso sin ningún vínculo no está configurado, aunque no exista un mínimo por pedido: se pide uno solo para marcarlo completo.
    private static bool HasTaxLink(SiigoSettings settings) =>
        settings.TaxLinks.Any(link => !string.IsNullOrWhiteSpace(link.SiigoTaxId))
        || settings.WithholdingLinks.Any(link => !string.IsNullOrWhiteSpace(link.SiigoTaxId));

    private static bool HasPaymentLink(SiigoSettings settings) =>
        // Crédito, contraentrega y por defecto ya no se usan al facturar (spec 2026-11-20): sólo cuentan los vínculos por cuenta de cobro.
        settings.PaymentLinks.Any(link => !string.IsNullOrWhiteSpace(link.SiigoPaymentTypeId));

    private static SiigoStepReadiness Of(SiigoSetupStep step, params string?[] missing)
    {
        var codes = missing.OfType<string>().ToArray();
        return new SiigoStepReadiness(step, codes.Length == 0, codes);
    }
}
