namespace Modules.Quotations.Domain;

/// <summary>Los códigos del borrador de cotización que crea una integración sin sesión (spec 2026-10-29 §8.3).</summary>
public static class ExternalQuotationErrors
{
    /// <summary>Lo lanza <c>QuotationsUnitOfWork</c> al chocar con <c>IX_quotations_tenant_external_key</c>. No llega a HTTP:
    /// <c>ExternalQuotationService</c> lo resuelve devolviendo la cotización que ya existe.</summary>
    public const string ExternalKeyTaken = "quotation.quotation.external_key_taken";
}
