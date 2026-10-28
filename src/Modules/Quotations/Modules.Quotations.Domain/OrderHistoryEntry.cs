namespace Modules.Quotations.Domain;

/// <summary>Mismo molde que <see cref="QuotationHistoryEntry"/>: lo que pasó con el pedido desde que
/// nació (spec 2026-10-15 §4.6). Lo anterior a la conversión queda en el historial de la cotización.</summary>
public sealed class OrderHistoryEntry
{
    public const int DetailsMaxLength = 500;

    private OrderHistoryEntry()
    {
    }

    private OrderHistoryEntry(
        OrderHistoryEntryId id,
        OrderId orderId,
        OrderHistoryEventType eventType,
        MemberId? memberId,
        string? details,
        DateTimeOffset occurredAt)
    {
        Id = id;
        OrderId = orderId;
        EventType = eventType;
        MemberId = memberId;
        Details = details is { Length: > DetailsMaxLength } ? details[..(DetailsMaxLength - 1)] + "…" : details;
        EventAt = occurredAt;
        CreatedAt = occurredAt;
    }

    public OrderHistoryEntryId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    public OrderHistoryEventType EventType { get; private set; }

    public DateTimeOffset EventAt { get; private set; }

    public MemberId? MemberId { get; private set; }

    public string? Details { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static OrderHistoryEntry Create(
        OrderHistoryEntryId id,
        OrderId orderId,
        OrderHistoryEventType eventType,
        MemberId? memberId,
        string? details,
        DateTimeOffset occurredAt) =>
        new(id, orderId, eventType, memberId, details, occurredAt);
}
