namespace Modules.Pos.Domain;

public sealed class PosPayment
{
    private PosPayment()
    {
    }

    public PosPaymentId Id { get; private set; }

    public PosSaleId SaleId { get; private set; }

    public int Position { get; private set; }

    public PosPaymentMethod Method { get; private set; }

    /// <summary>Lo aplicado al total. En Cash lo calcula el servidor (cashDue).</summary>
    public decimal Amount { get; private set; }

    /// <summary>Sólo Cash: el billete que entregó el cliente.</summary>
    public decimal? Tendered { get; private set; }

    /// <summary>Sólo Card/Transfer, opcional.</summary>
    public string? Reference { get; private set; }

    internal static PosPayment Create(
        PosSaleId saleId, int position, PosPaymentMethod method, decimal amount, decimal? tendered, string? reference) =>
        new()
        {
            Id = PosPaymentId.New(),
            SaleId = saleId,
            Position = position,
            Method = method,
            Amount = amount,
            Tendered = tendered,
            Reference = reference,
        };
}
