namespace Modules.Pos.Domain;

public readonly record struct CashSessionId(Guid Value)
{
    public static CashSessionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Id de la venta. Lo genera el cliente (crypto.randomUUID() al abrir el cobro) y es la clave de
/// idempotencia del POST (spec, «Crear venta — idempotencia por id de cliente»).
/// </summary>
public readonly record struct PosSaleId
{
    public PosSaleId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new PosDomainException("pos.sale.id_required", "The sale id is required.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static PosSaleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct PosSaleLineId(Guid Value)
{
    public static PosSaleLineId New() => new(Guid.CreateVersion7());
}

public readonly record struct PosPaymentId(Guid Value)
{
    public static PosPaymentId New() => new(Guid.CreateVersion7());
}
