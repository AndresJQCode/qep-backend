using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>Quién originó la recepción: la consola (<c>Manual</c>) o la recepción con la app del WMS (<c>WmsReceipt</c>, T3).</summary>
public static class PurchaseOrderEventOrigins
{
    public const string Manual = "Manual";

    public const string WmsReceipt = "WmsReceipt";
}

/// <summary>
/// <c>purchases.order.received.v1</c> en el outbox de plataforma, en el mismo <c>SaveChanges</c> que el cambio de estado.
/// El inventario básico del WMS lo consume para sumar lo recibido a la bodega principal.
/// </summary>
public interface IPurchaseOrderEventPublisher
{
    /// <summary>Se llama justo después de <see cref="PurchaseOrder.Receive"/>: la versión del evento es la que dejó esa mutación.</summary>
    void PublishReceived(PurchaseOrder order, string origin, DateTimeOffset occurredAt);
}

/// <summary>
/// El contrato de <c>purchases.order.received.v1</c>. Los nombres en camelCase son el contrato con el WMS: System.Text.Json
/// serializa los records con el nombre del parámetro tal cual y el consumidor los lee por nombre.
/// </summary>
public sealed record PurchaseOrderReceivedEvent(
    Guid tenantId,
    Guid purchaseOrderId,
    long version,
    string origin,
    DateTimeOffset occurredAt,
    IReadOnlyList<PurchaseOrderReceivedLine> lines)
{
    public const string EventName = "purchases.order.received.v1";

    /// <summary>
    /// Viajan las cantidades <b>confirmadas</b> por el proveedor, no las pedidas, y sin las líneas de cantidad confirmada 0 (las
    /// rechazadas). Una línea vinculada al mayorista puede no tener variante todavía: viaja con <c>variantId</c> nulo y el
    /// consumidor la aparta.
    /// </summary>
    public static PurchaseOrderReceivedEvent From(PurchaseOrder order, string origin, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new PurchaseOrderReceivedEvent(
            order.TenantId,
            order.Id.Value,
            order.Version,
            origin,
            occurredAt,
            [.. order.Lines
                .Where(line => line.ConfirmedQuantity is > 0m)
                .Select(line => new PurchaseOrderReceivedLine(line.Id, line.VariantId, line.ConfirmedQuantity!.Value))]);
    }
}

public sealed record PurchaseOrderReceivedLine(Guid lineId, Guid? variantId, decimal quantity);
