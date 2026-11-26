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

    /// <summary>409: el mayorista ya aprobó, preparó, facturó o archivó el pedido de la orden vinculada (D-12).</summary>
    public const string NotCancellable = "purchases.order.not_cancellable";

    /// <summary>El proveedor no es un tenant vinculado: la acción es sólo de órdenes vinculadas.</summary>
    public const string SupplierNotLinked = "purchases.order.supplier_not_linked";

    /// <summary>409: el motor de pedidos del mayorista rechazó el alta (D-15); el código interno va al log.</summary>
    public const string WholesalerRejected = "purchases.order.wholesaler_rejected";

    public const string ProductNotVisible = "wholesale.product.not_visible";

    /// <summary>422: el <c>categoryId</c> no está en el árbol que la lista del distribuidor le deja ver (spec 2026-11-20).</summary>
    public const string CategoryNotVisible = "wholesale.category.not_visible";

    public const string RelationshipInactive = "wholesale.relationship.inactive";

    /// <summary>409: el correo al proveedor sólo vale desde que la orden se envió y hasta antes de cancelarla.</summary>
    public const string EmailNotAllowed = "purchases.order.email_not_allowed";

    /// <summary>422: reenviar sin que el proveedor tenga correo guardado.</summary>
    public const string SupplierEmailMissing = "purchases.order.supplier_email_missing";

    /// <summary>502: <c>qcode-pdf</c> no devolvió el PDF. No está en el contrato del spec (pendiente del owner, R8).</summary>
    public const string PdfRenderFailed = "purchases.order.pdf_render_failed";
}
