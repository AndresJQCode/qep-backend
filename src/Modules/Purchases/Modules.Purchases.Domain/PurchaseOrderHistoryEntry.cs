namespace Modules.Purchases.Domain;

/// <summary>
/// Un paso del ciclo de la orden: a qué estado pasó, cuándo y qué miembro lo hizo. <see cref="Sequence"/> da el orden
/// estable en base, donde dos pasos pueden caer en el mismo instante.
/// </summary>
public sealed record PurchaseOrderHistoryEntry(PurchaseOrderStatus Status, DateTimeOffset OccurredAt, Guid MemberId)
{
    /// <summary>Posición en el historial, desde 1 para el <c>Draft</c> del alta.</summary>
    public int Sequence { get; init; }
}
