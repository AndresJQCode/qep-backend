namespace Modules.Quotations.Domain;

/// <summary>
/// Línea de tiempo de una cotización (modelo-datos-cotizaciones.md §2.3) — fuente de verdad de
/// quién creó o modificó la cotización y cuándo. Entidad de primer nivel, no hija de
/// <see cref="Quotation"/>: se escribe en la misma transacción que la mutación, pero no hace
/// falta cargarla cada vez que se edita una cotización, así que no vive en su agregado.
/// </summary>
public sealed class QuotationHistoryEntry
{
    private QuotationHistoryEntry()
    {
    }

    private QuotationHistoryEntry(
        QuotationHistoryEntryId id,
        QuotationId quotationId,
        QuotationHistoryEventType eventType,
        MemberId? memberId,
        string? details,
        DateTimeOffset occurredAt)
    {
        Id = id;
        QuotationId = quotationId;
        EventType = eventType;
        MemberId = memberId;
        Details = details;
        EventAt = occurredAt;
        CreatedAt = occurredAt;
    }

    public QuotationHistoryEntryId Id { get; private set; }

    public QuotationId QuotationId { get; private set; }

    public QuotationHistoryEventType EventType { get; private set; }

    public DateTimeOffset EventAt { get; private set; }

    /// <summary>Null para <see cref="QuotationHistoryEventType.Expired"/>: el vencimiento lo
    /// dispara un job programado (US-19), no una persona.</summary>
    public MemberId? MemberId { get; private set; }

    public string? Details { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static QuotationHistoryEntry Create(
        QuotationHistoryEntryId id,
        QuotationId quotationId,
        QuotationHistoryEventType eventType,
        MemberId? memberId,
        string? details,
        DateTimeOffset occurredAt) =>
        new(id, quotationId, eventType, memberId, details, occurredAt);
}
