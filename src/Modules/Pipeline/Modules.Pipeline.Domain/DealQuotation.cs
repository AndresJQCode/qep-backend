namespace Modules.Pipeline.Domain;

/// <summary>
/// Spec 2026-10-13 §4.4: la foto que Pipeline guarda de cada cotización que le llegó por evento. Una cotización pertenece a lo
/// sumo a un negocio (PK <c>(tenant_id, quotation_id)</c>). Con ella el tablero muestra el número de la principal sin leer
/// Quotations. <see cref="DealQuotationStatus.Converted"/> no vuelve a <see cref="DealQuotationStatus.Sent"/>: un sent que llega
/// tarde (reintento) no deshace la conversión.
/// </summary>
public sealed class DealQuotation
{
    public const int NumberMaxLength = 40;

    private DealQuotation()
    {
        QuotationNumber = string.Empty;
        Currency = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public Guid QuotationId { get; private set; }

    public Guid? DealId { get; private set; }

    public Guid CustomerId { get; private set; }

    public string QuotationNumber { get; private set; }

    public decimal Total { get; private set; }

    public string Currency { get; private set; }

    public DealQuotationStatus Status { get; private set; }

    public Guid? OrderId { get; private set; }

    public DateTimeOffset? OrderCancelledAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DealQuotation Create(
        Guid tenantId, Guid quotationId, string quotationNumber, Guid customerId, decimal total, string currency, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            QuotationId = quotationId,
            QuotationNumber = quotationNumber,
            CustomerId = customerId,
            Total = total,
            Currency = currency,
            Status = DealQuotationStatus.Sent,
            UpdatedAt = now,
        };

    public void Refresh(
        string quotationNumber, Guid customerId, decimal total, string currency, bool converted, Guid? orderId, DateTimeOffset now)
    {
        QuotationNumber = quotationNumber;
        CustomerId = customerId;
        Total = total;
        Currency = currency;
        if (converted)
        {
            Status = DealQuotationStatus.Converted;

            // Un converted repetido o desordenado (sin pedido, o del mismo pedido) no borra ni el pedido ni su anulación: sólo un
            // pedido distinto es una conversión nueva y deja sin efecto la anulación anterior.
            if (orderId is { } newOrderId && newOrderId != OrderId)
            {
                OrderId = newOrderId;
                OrderCancelledAt = null;
            }
        }

        UpdatedAt = now;
    }

    public void LinkTo(Guid dealId, DateTimeOffset now)
    {
        DealId = dealId;
        UpdatedAt = now;
    }

    public void Unlink(DateTimeOffset now)
    {
        DealId = null;
        UpdatedAt = now;
    }

    /// <summary>Marca como anulado el pedido <paramref name="orderId"/>. Sin pedido conocido (el order.cancelled llegó antes que el
    /// converted) lo adopta, así el converted del mismo pedido no se toma por una conversión nueva. Si el pedido vigente es otro,
    /// el aviso es viejo y no cambia nada.</summary>
    /// <returns><c>false</c> si el aviso no es del pedido vigente y se ignoró.</returns>
    public bool MarkOrderCancelled(Guid orderId, DateTimeOffset now)
    {
        if (OrderId is { } current && current != orderId)
        {
            return false;
        }

        OrderId = orderId;
        OrderCancelledAt = now;
        UpdatedAt = now;
        return true;
    }
}
