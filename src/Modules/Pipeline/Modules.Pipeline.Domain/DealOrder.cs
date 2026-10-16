namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-15 §7: la foto de un pedido sin cotización (manual o de una integración), paralela a <see cref="DealQuotation"/>.
/// Un pedido que viene de una cotización no deja foto acá: el <c>converted</c> de la cotización ya actuó sobre el negocio.
/// PK <c>(tenant_id, order_id)</c>: un <c>order.created</c> repetido encuentra la foto y no crea un segundo negocio.
/// </summary>
public sealed class DealOrder
{
    public const int NumberMaxLength = 40;
    public const int SourceMaxLength = 20;

    private DealOrder()
    {
        OrderNumber = string.Empty;
        Currency = string.Empty;
        Source = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid? DealId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string OrderNumber { get; private set; }

    public decimal Total { get; private set; }

    public string Currency { get; private set; }

    /// <summary>El <c>OrderSource</c> de Quotations por nombre (<c>Manual</c>, <c>Shopify</c>…): Pipeline no conoce el enum.</summary>
    public string Source { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DealOrder Create(
        Guid tenantId, Guid orderId, string orderNumber, Guid customerId, decimal total, string currency, string source, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new DealOrder
        {
            TenantId = tenantId,
            OrderId = orderId,
            OrderNumber = orderNumber,
            CustomerId = customerId,
            Total = total,
            Currency = currency,
            Source = source,
            UpdatedAt = now,
        };
    }

    public void LinkTo(Guid dealId, DateTimeOffset now)
    {
        DealId = dealId;
        UpdatedAt = now;
    }

    /// <returns><c>false</c> si ya estaba anulado: un aviso repetido no vuelve a reabrir nada.</returns>
    public bool MarkCancelled(DateTimeOffset now)
    {
        if (CancelledAt is not null)
        {
            return false;
        }

        CancelledAt = now;
        UpdatedAt = now;
        return true;
    }
}
