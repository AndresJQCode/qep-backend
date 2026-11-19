namespace Modules.Siigo.Domain;

/// <summary>Los seis pasos del asistente y de la pantalla de configuración (spec 2026-11-18 §5.3), en el orden en que se recorren.</summary>
public enum SiigoSetupStep
{
    Credentials,
    Company,
    Document,
    Taxes,
    Payments,
    Products,
}

/// <summary>Un paso: completo, o incompleto con los códigos de lo que falta (<c>snake_case</c>, los traduce la pantalla).</summary>
public sealed record SiigoStepReadiness(SiigoSetupStep Step, bool Complete, IReadOnlyList<string> MissingCodes);

/// <summary>
/// Spec 2026-11-18 §5.3: el <c>readiness</c> por paso que devuelve <c>GET settings</c>. Sólo mira lo que la fila sabe de sí misma:
/// <c>Credentials</c> lo decide la conexión de Integrations (el handler lo ajusta) y <c>Products</c> informa la cobertura sin bloquear.
/// Impuestos y formas de pago no tienen un mínimo propio: qué vínculo hace falta depende del pedido, y cada faltante sale al generar la
/// factura (<c>siigo.invoice.tax_unlinked</c>, <c>payment_unlinked</c>…), con el pedido a la vista.
/// </summary>
public static class SiigoSettingsReadiness
{
    public const string CompanyMissing = "company_missing";
    public const string DocumentTypeMissing = "document_type_missing";
    public const string SellerMissing = "seller_missing";

    public static IReadOnlyList<SiigoStepReadiness> Evaluate(SiigoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return
        [
            Of(SiigoSetupStep.Credentials),
            Of(SiigoSetupStep.Company, settings.CompanyId is null ? CompanyMissing : null),
            Of(
                SiigoSetupStep.Document,
                settings.DocumentTypeId is null ? DocumentTypeMissing : null,
                settings.SellerId is null ? SellerMissing : null),
            Of(SiigoSetupStep.Taxes),
            Of(SiigoSetupStep.Payments),
            Of(SiigoSetupStep.Products),
        ];
    }

    private static SiigoStepReadiness Of(SiigoSetupStep step, params string?[] missing)
    {
        var codes = missing.OfType<string>().ToArray();
        return new SiigoStepReadiness(step, codes.Length == 0, codes);
    }
}
