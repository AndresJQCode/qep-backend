using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El historial y la auditoría de los cambios de línea, compartidos por el guardado de una vez y por la creación
/// con líneas (spec 2026-10-18 §7.4, D-L2): la línea de tiempo de una cotización no puede depender de por qué
/// endpoint entró el cambio.
/// </summary>
internal static class QuotationItemEditHistory
{
    // Una fila de historial Edited y una acción quotation.quotation.item_* por cada cambio, como
    // los endpoints de línea de hoy. El nombre de una baja se busca en una sola ida al catálogo,
    // igual que BatchUpdateQuotationItemsHandler.
    public static async Task RecordAsync(
        IQuotationRepository repository,
        IQuotationAuditPublisher auditPublisher,
        IQuotationProductLookup productLookup,
        Guid actorId,
        Guid tenantId,
        Quotation quotation,
        IReadOnlyList<QuotationItemEdit> edits,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var removedProductIds = edits
            .Where(edit => edit.Kind == QuotationItemEditKind.Removed)
            .Select(edit => edit.ProductId)
            .ToArray();
        var removedProducts = removedProductIds.Length == 0
            ? new Dictionary<Guid, QuotationProductRef>()
            : await productLookup.FindManyAsync(tenantId, removedProductIds, cancellationToken);

        foreach (var edit in edits)
        {
            string summary;
            string action;
            if (edit.Kind == QuotationItemEditKind.Added)
            {
                summary = QuotationChangeSummary.ItemAdded(edit.ProductName!, edit.Quantity!.Value);
                action = "quotation.quotation.item_added";
            }
            else if (edit.Kind == QuotationItemEditKind.QuantityChanged)
            {
                summary = QuotationChangeSummary.ItemQuantityChanged(
                    edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value);
                action = "quotation.quotation.item_updated";
            }
            else
            {
                var productName = removedProducts.TryGetValue(edit.ProductId, out var product)
                    ? product.Name
                    : "un producto";
                summary = QuotationChangeSummary.ItemRemoved(productName);
                action = "quotation.quotation.item_removed";
            }

            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(), quotation.Id, QuotationHistoryEventType.Edited,
                updatedBy, summary, now));
            auditPublisher.Publish(
                tenantId, actorId, action, quotation.Id.ToString(), "success", now);
        }
    }
}
