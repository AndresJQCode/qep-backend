using FluentValidation;
using FluentValidation.Results;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Las líneas armadas, la moneda efectiva y el modo de la orden. <c>SupplierTenantId</c> es el mayorista con que la orden
/// queda vinculada al guardarse (D-10); null en una externa.
/// </summary>
public sealed record PurchaseOrderLineBuild(
    string Currency,
    Guid? SupplierTenantId,
    IReadOnlyList<PurchaseOrderLineDraft> Lines,
    bool ShowPrices = true);

/// <summary>
/// El armado de líneas que comparten el alta, la edición y la vista previa (D-19): las reglas que miran otros módulos, en el
/// orden del contrato, y los borradores con código, nombre y tasa actuales del catálogo. Así la vista previa no puede pintar un
/// total distinto del que el alta guarda.
/// <para>Mayorista fase 3: con un proveedor vinculado (<see cref="PurchasesSupplierRef.LinkedTenantId"/>) las variantes son del
/// mayorista y el precio sale de su lista (<see cref="IWholesaleCatalog"/>, que verifica la relación); la moneda y el costo del
/// cuerpo se ignoran (D-11) y el costo guardado es la lista sin IVA (D-9).</para>
/// </summary>
public sealed class PurchaseOrderLineBuilder(
    IPurchasesSupplierLookup suppliers,
    IPurchasesCatalogLookup catalog,
    IWholesaleCatalog wholesaleCatalog)
{
    /// <summary>
    /// Moneda (422 <c>invalid_currency</c>, D-17; no aplica con proveedor vinculado) → proveedor (404
    /// <c>suppliers.supplier.not_found</c>, D-3; inactivo, 422 <c>supplier_inactive</c>) → <c>lines</c> vacía (422 <c>empty</c>)
    /// → variantes. Externo: del tenant en cualquier estado (422 <c>line_not_in_catalog</c>, D-6) y costo obligatorio (422
    /// <c>Lines[i].UnitCost</c>). Vinculado: visibles en la lista del distribuidor (403/422 del catálogo del mayorista).
    /// </summary>
    public async Task<PurchaseOrderLineBuild> BuildAsync(
        Guid tenantId, IPurchaseOrderWriteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var supplier = await suppliers.FindAsync(tenantId, command.SupplierId, cancellationToken);
        var wholesalerTenantId = supplier?.LinkedTenantId;
        if (wholesalerTenantId is null && !Currencies.IsSupported(command.Currency))
        {
            throw new PurchasesDomainException(
                PurchasesErrorCodes.InvalidCurrency, $"Currency '{command.Currency}' is not supported.");
        }

        if (supplier is null)
        {
            throw PurchasesConflicts.SupplierNotFound(command.SupplierId);
        }

        if (!supplier.IsActive)
        {
            throw SupplierInactive();
        }

        var lines = command.Lines ?? [];
        if (lines.Count == 0)
        {
            throw new PurchasesDomainException(PurchasesErrorCodes.Empty, "A purchase order needs at least one line.");
        }

        return wholesalerTenantId is { } wholesaler
            ? await BuildLinkedAsync(tenantId, wholesaler, lines, cancellationToken)
            : new PurchaseOrderLineBuild(command.Currency, null, await BuildExternalAsync(tenantId, lines, cancellationToken));
    }

    internal static PurchasesDomainException SupplierInactive() =>
        new(PurchasesErrorCodes.SupplierInactive, "The supplier is inactive.");

    private async Task<List<PurchaseOrderLineDraft>> BuildExternalAsync(
        Guid tenantId, IReadOnlyList<PurchaseOrderLineInput> lines, CancellationToken cancellationToken)
    {
        // El costo es opcional en la validación (D-11) y obligatorio con proveedor externo: el error va al campo, como si lo
        // hubiera dado el validador.
        var missingCosts = lines
            .Select((line, index) => (line, index))
            .Where(pair => pair.line.UnitCost is null)
            .Select(pair => new ValidationFailure($"Lines[{pair.index}].UnitCost", "The unit cost is required."))
            .ToList();
        if (missingCosts.Count > 0)
        {
            throw new ValidationException(missingCosts);
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
                line.VariantId, variant.Code, variant.Name, line.Quantity, line.UnitCost!.Value, variant.TaxRatePercent));
        }

        return drafts;
    }

    private async Task<PurchaseOrderLineBuild> BuildLinkedAsync(
        Guid tenantId, Guid wholesalerTenantId, IReadOnlyList<PurchaseOrderLineInput> lines, CancellationToken cancellationToken)
    {
        var (currency, items, showPrices) = await wholesaleCatalog.PriceAsync(
            tenantId, wholesalerTenantId, lines.Select(line => line.VariantId).Distinct().ToArray(), cancellationToken);
        var drafts = new List<PurchaseOrderLineDraft>(lines.Count);
        foreach (var line in lines)
        {
            // El adaptador ya lanza por una variante que no ve; esto es la red si un día devolviera menos.
            if (!items.TryGetValue(line.VariantId, out var item))
            {
                throw new PurchasesDomainException(
                    PurchasesErrorCodes.ProductNotVisible, "One of the products is not available from this wholesaler.");
            }

            drafts.Add(new PurchaseOrderLineDraft(
                null,
                item.Code,
                item.Name,
                line.Quantity,
                PurchaseOrderMath.NetOfVat(item.ListPrice, item.TaxRatePercent),
                item.TaxRatePercent,
                new LinkedLinePrice(item.VariantId, item.BasePrice, item.DiscountPercent, item.ListPrice)));
        }

        return new PurchaseOrderLineBuild(currency, wholesalerTenantId, drafts, showPrices);
    }
}
