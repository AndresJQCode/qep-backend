namespace Modules.ServiceContracts.Domain;

/// <summary>
/// Una fila del historial de precios de un servicio: de qué precio y moneda a cuáles, quién lo cambió y por qué. La escribe
/// sólo <see cref="ServiceContract.Update"/> cuando el precio o la moneda cambian de verdad, y nunca se edita ni se borra: es
/// la justificación del cambio, que el equipo relee cuando un cliente pregunta por qué paga distinto.
/// </summary>
public sealed class ServicePriceChange
{
    public const int ReasonMaxLength = 500;

    // EF Core materializa por acá; Record es el único punto de entrada de código.
    private ServicePriceChange()
    {
        PreviousCurrency = string.Empty;
        NewCurrency = string.Empty;
        Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ServiceContractId ServiceId { get; private set; }

    public decimal PreviousPrice { get; private set; }

    public string PreviousCurrency { get; private set; }

    public decimal NewPrice { get; private set; }

    public string NewCurrency { get; private set; }

    /// <summary>Obligatorio, ya recortado, hasta <see cref="ReasonMaxLength"/>.</summary>
    public string Reason { get; private set; }

    /// <summary>
    /// La membresía de quien editó, como <c>Charge.PaidByMemberId</c>. Nula si quien editó no tenía membresía activa en el
    /// tenant (un operador): la auditoría conserva el sujeto, y acá no hay a quién mostrarle.
    /// </summary>
    public Guid? ChangedByMemberId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    internal static ServicePriceChange Record(
        ServiceContract service,
        decimal previousPrice,
        string previousCurrency,
        string reason,
        Guid? changedByMemberId,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = service.TenantId,
            ServiceId = service.Id,
            PreviousPrice = previousPrice,
            PreviousCurrency = previousCurrency,
            NewPrice = service.Price,
            NewCurrency = service.Currency,
            Reason = reason,
            ChangedByMemberId = changedByMemberId,
            OccurredAt = now,
        };
}
