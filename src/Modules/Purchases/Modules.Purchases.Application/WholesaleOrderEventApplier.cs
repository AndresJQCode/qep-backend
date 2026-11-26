using Microsoft.Extensions.Logging;
using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>
/// Mayorista fase 3: lo que hace en el comprador el evento del mayorista, ya leído del inbox. No abre transacción: la abre el
/// procesador (Task 7), que marca el evento procesado según el <see cref="WholesaleEventOutcome"/>.
/// <list type="bullet">
/// <item>D-20: un evento que no calza (orden de otro comprador, de otro mayorista o de otro pedido) se ignora y se registra.</item>
/// <item>Ruling X8b: si la orden sigue en borrador, el envío no terminó de guardarse (la ventana de D-8): se reintenta.</item>
/// <item>Ruling X2: crear productos nunca frena la confirmación. Las líneas se agrupan por código recortado (un producto por
/// código) y una falla deja esas líneas sin variante, registrada, y la confirmación se guarda igual.</item>
/// </list>
/// </summary>
public sealed partial class WholesaleOrderEventApplier(
    IPurchaseOrderRepository orders,
    IPurchasesCatalogWriter catalogWriter,
    IPurchasesProductImages productImages,
    IPurchasesAuditPublisher auditPublisher,
    IPurchasesUnitOfWork unitOfWork,
    ILogger<WholesaleOrderEventApplier> logger)
{
    // D-22: la traza queda en el comprador, firmada por nadie del tenant.
    private static readonly string[] WholesalerField = ["wholesaler"];

    // Spec 2026-11-21: las fotos de los productos que esta aprobación creó. El applier es del scope de un mensaje, así que la
    // lista es de ese mensaje; el procesador las copia sólo después de confirmar.
    private readonly List<PendingProductImage> _pendingImages = [];

    /// <summary>
    /// Copia la foto de cada producto que la última aprobación creó. Se llama **después** del commit: nunca lanza (el mensaje ya
    /// quedó procesado) y una falla no impide las demás; cada una se registra.
    /// </summary>
    public async Task CopyPendingImagesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = _pendingImages.ToArray();
        _pendingImages.Clear();
        foreach (var image in pending)
        {
            try
            {
                await productImages.CopyAsync(image, now, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogImageCopyFailed(logger, exception, image.BuyerTenantId, image.ProductId);
            }
        }
    }

    public async Task<WholesaleEventOutcome> ApplyApprovedAsync(
        WholesaleApproval approval, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var (order, outcome) = await MatchAsync(
            approval.BuyerTenantId, approval.PurchaseOrderId, approval.WholesalerTenantId, approval.OrderId, cancellationToken);
        if (order is null)
        {
            return outcome;
        }

        // Revisión del bloque B, M2: costos en otra moneda nunca se aplican en silencio, y reintentar no lo arregla.
        if (approval.Currency is { } currency && !string.Equals(currency, order.Currency, StringComparison.OrdinalIgnoreCase))
        {
            LogCurrencyMismatch(logger, approval.BuyerTenantId, approval.PurchaseOrderId, currency, order.Currency);
            return WholesaleEventOutcome.Ignored;
        }

        // El costo de la orden es sin IVA (D-9); la tasa que viaja es la efectiva de la línea del pedido.
        var finalLines = approval.Lines
            .Select(line => new WholesalerFinalLine(
                line.SourcePurchaseOrderLineId,
                line.VariantId,
                line.Code,
                line.Name,
                line.Quantity,
                PurchaseOrderMath.NetOfVat(line.UnitPrice, line.TaxPercentage),
                line.TaxPercentage))
            .ToList();
        if (!order.ApplyWholesalerApproval(finalLines, now))
        {
            // D-21: repetido o fuera de orden (ya confirmada, recibida o cancelada): nada cambia.
            LogRepeated(logger, approval.BuyerTenantId, approval.PurchaseOrderId, order.Status);
            return WholesaleEventOutcome.Ignored;
        }

        await LinkCatalogAsync(order, now, cancellationToken);
        Audit(order, PurchasesAuditActions.Confirmed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return WholesaleEventOutcome.Applied;
    }

    public async Task<WholesaleEventOutcome> ApplyCancelledAsync(
        WholesaleCancellation cancellation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var (order, outcome) = await MatchAsync(
            cancellation.BuyerTenantId, cancellation.PurchaseOrderId, cancellation.WholesalerTenantId, cancellation.OrderId,
            cancellationToken);
        if (order is null)
        {
            return outcome;
        }

        // Ya cancelada (la canceló el distribuidor, que es quien emitió este evento de vuelta) o recibida: nada cambia.
        if (!order.ApplyWholesalerCancellation(now))
        {
            LogRepeated(logger, cancellation.BuyerTenantId, cancellation.PurchaseOrderId, order.Status);
            return WholesaleEventOutcome.Ignored;
        }

        Audit(order, PurchasesAuditActions.Cancelled, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return WholesaleEventOutcome.Applied;
    }

    // La orden se busca en el tenant del comprador que dice el evento; después se comprueba que la emitió su mayorista y que
    // es su pedido. Sólo un borrador vinculado de ese mayorista y sin pedido todavía se reintenta.
    private async Task<(PurchaseOrder? Order, WholesaleEventOutcome Outcome)> MatchAsync(
        Guid buyerTenantId,
        Guid purchaseOrderId,
        Guid wholesalerTenantId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(buyerTenantId, new PurchaseOrderId(purchaseOrderId), cancellationToken);
        if (order is null || order.SupplierTenantId != wholesalerTenantId)
        {
            LogMismatch(logger, buyerTenantId, purchaseOrderId, wholesalerTenantId, orderId);
            return (null, WholesaleEventOutcome.Ignored);
        }

        if (order is { Status: PurchaseOrderStatus.Draft, LinkedOrderId: null })
        {
            LogNotSentYet(logger, buyerTenantId, purchaseOrderId, orderId);
            return (null, WholesaleEventOutcome.RetryLater);
        }

        if (order.LinkedOrderId != orderId)
        {
            LogMismatch(logger, buyerTenantId, purchaseOrderId, wholesalerTenantId, orderId);
            return (null, WholesaleEventOutcome.Ignored);
        }

        return (order, WholesaleEventOutcome.Applied);
    }

    // D-18 y ruling X2: un producto por código recortado; una falla deja esas líneas sin variante y sigue con las demás.
    private async Task LinkCatalogAsync(PurchaseOrder order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var byCode = order.LinesNeedingCatalog()
            .GroupBy(line => line.Code.Trim(), StringComparer.Ordinal)
            .ToList();
        foreach (var group in byCode)
        {
            var first = group.First();
            if (group.Key.Length == 0)
            {
                LogBlankCode(logger, order.TenantId, order.Id.Value, first.Id);
                continue;
            }

            PurchasesCatalogEntry entry;
            try
            {
                entry = await catalogWriter.EnsureVariantAsync(
                    order.TenantId, group.Key, first.Name, order.Currency, first.ConfirmedUnitCost, now, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCatalogFailed(logger, exception, order.TenantId, order.Id.Value, group.Key);
                continue;
            }

            // Sólo la primera línea del código cuenta como «creada»: es un producto, no uno por línea.
            foreach (var line in group)
            {
                order.LinkLineToCatalog(line.Id, entry.VariantId, entry.Created && line.Id == first.Id);
            }

            // La foto sale de la variante del mayorista de la primera línea que la conozca; sólo a lo que se creó.
            if (entry is { Created: true, ProductId: { } productId }
                && order.SupplierTenantId is { } supplierTenant
                && group.FirstOrDefault(line => line.SupplierVariantId is not null)?.SupplierVariantId is { } supplierVariant)
            {
                _pendingImages.Add(new PendingProductImage(order.TenantId, supplierTenant, supplierVariant, productId));
            }
        }
    }

    private void Audit(PurchaseOrder order, string action, DateTimeOffset now) =>
        auditPublisher.Publish(
            order.TenantId,
            Guid.Empty,
            action,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            "success",
            WholesalerField,
            now);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wholesale event for purchase order {PurchaseOrderId} of tenant {TenantId} does not match it (wholesaler {WholesalerTenantId}, order {OrderId}); ignored.")]
    private static partial void LogMismatch(
        ILogger logger, Guid tenantId, Guid purchaseOrderId, Guid wholesalerTenantId, Guid orderId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Wholesale approval for purchase order {PurchaseOrderId} of tenant {TenantId} is in {EventCurrency} but the order is in {OrderCurrency}; it is not applied.")]
    private static partial void LogCurrencyMismatch(
        ILogger logger, Guid tenantId, Guid purchaseOrderId, string eventCurrency, string orderCurrency);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wholesale event for purchase order {PurchaseOrderId} of tenant {TenantId} (order {OrderId}) found it still in draft; it will be retried.")]
    private static partial void LogNotSentYet(ILogger logger, Guid tenantId, Guid purchaseOrderId, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wholesale event for purchase order {PurchaseOrderId} of tenant {TenantId} changed nothing: the order is {Status}.")]
    private static partial void LogRepeated(ILogger logger, Guid tenantId, Guid purchaseOrderId, PurchaseOrderStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Line {LineId} of purchase order {PurchaseOrderId} of tenant {TenantId} has no code; it stays without a catalog product.")]
    private static partial void LogBlankCode(ILogger logger, Guid tenantId, Guid purchaseOrderId, Guid lineId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not copy the wholesaler image to catalog product {ProductId} of tenant {TenantId}; the product stays without an image.")]
    private static partial void LogImageCopyFailed(ILogger logger, Exception exception, Guid tenantId, Guid productId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not find or create the catalog product {Code} for purchase order {PurchaseOrderId} of tenant {TenantId}; its lines stay without a catalog product.")]
    private static partial void LogCatalogFailed(
        ILogger logger, Exception exception, Guid tenantId, Guid purchaseOrderId, string code);
}
