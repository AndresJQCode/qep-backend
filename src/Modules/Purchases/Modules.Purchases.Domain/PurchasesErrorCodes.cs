namespace Modules.Purchases.Domain;

/// <summary>
/// Códigos estables de error del módulo (contrato HTTP de compras, § Errores). El mapeo a HTTP vive en
/// <c>ApiExceptionHandler</c> y en los handlers; acá sólo el contrato.
/// </summary>
public static class PurchasesErrorCodes
{
    public const string NotFound = "purchases.order.not_found";

    public const string NotDraft = "purchases.order.not_draft";

    public const string InvalidTransition = "purchases.order.invalid_transition";

    public const string Empty = "purchases.order.empty";

    public const string LineNotInCatalog = "purchases.order.line_not_in_catalog";

    public const string SupplierInactive = "purchases.order.supplier_inactive";

    public const string InvalidCurrency = "purchases.order.invalid_currency";

    public const string ConfirmLinesMismatch = "purchases.order.confirm_lines_mismatch";

    /// <summary>502: <c>qcode-pdf</c> no devolvió el PDF. No está en el contrato del spec (pendiente del owner, R8).</summary>
    public const string PdfRenderFailed = "purchases.order.pdf_render_failed";
}
