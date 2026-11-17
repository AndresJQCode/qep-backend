using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// El armado de líneas que comparten el alta, la edición y la vista previa (D-19): las reglas que miran otros módulos, en el
/// orden del contrato, y los borradores con código, nombre y tasa actuales del catálogo. Así la vista previa no puede pintar un
/// total distinto del que el alta guarda.
/// </summary>
public sealed class PurchaseOrderLineBuilder(IPurchasesSupplierLookup suppliers, IPurchasesCatalogLookup catalog)
{
    /// <summary>
    /// Moneda (422 <c>invalid_currency</c>, D-17) → proveedor (404 <c>suppliers.supplier.not_found</c>, D-3; inactivo, 422
    /// <c>supplier_inactive</c>) → <c>lines</c> vacía (422 <c>empty</c>) → variantes del tenant en cualquier estado (422
    /// <c>line_not_in_catalog</c>, D-6). Devuelve los borradores en el orden del cuerpo.
    /// </summary>
    public async Task<IReadOnlyList<PurchaseOrderLineDraft>> BuildAsync(
        Guid tenantId, IPurchaseOrderWriteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Currencies.IsSupported(command.Currency))
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.InvalidCurrency, $"Currency '{command.Currency}' is not supported.");
        }

        var supplier = await suppliers.FindAsync(tenantId, command.SupplierId, cancellationToken)
            ?? throw PurchasesConflicts.SupplierNotFound(command.SupplierId);
        if (!supplier.IsActive)
        {
            throw SupplierInactive();
        }

        var lines = command.Lines ?? [];
        if (lines.Count == 0)
        {
            throw new PurchasesDomainException(PurchasesErrorCodes.Empty, "A purchase order needs at least one line.");
        }

        var variantIds = lines.Select(line => line.VariantId).Distinct().ToArray();
        var variants = await catalog.FindVariantsAsync(tenantId, variantIds, cancellationToken);
        var drafts = new List<PurchaseOrderLineDraft>(lines.Count);
        foreach (var line in lines)
        {
            if (!variants.TryGetValue(line.VariantId, out var variant))
            {
                throw new PurchasesDomainException(
                    PurchasesErrorCodes.LineNotInCatalog, $"Variant '{line.VariantId}' is not in the catalog.");
            }

            drafts.Add(new PurchaseOrderLineDraft(
                line.VariantId, variant.Code, variant.Name, line.Quantity, line.UnitCost, variant.TaxRatePercent));
        }

        return drafts;
    }

    internal static PurchasesDomainException SupplierInactive() =>
        new(PurchasesErrorCodes.SupplierInactive, "The supplier is inactive.");
}
