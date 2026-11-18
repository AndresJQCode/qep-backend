namespace Modules.Purchases.Domain;

/// <summary>
/// Una línea por guardar: la variante y lo que de ella se copia del catálogo (código, nombre y tasa de IVA, como foto),
/// más la cantidad y el costo sin IVA pedidos. Application la arma; el dominio no mira el catálogo.
/// </summary>
public sealed record PurchaseOrderLineDraft(
    Guid VariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitCost,
    decimal TaxRatePercent);

/// <summary>Lo que el proveedor confirmó para una línea. Cantidad 0 la rechaza.</summary>
public sealed record PurchaseOrderLineConfirmation(Guid LineId, decimal ConfirmedQuantity, decimal ConfirmedUnitCost);

/// <summary>
/// Una línea de la orden. Guarda código, nombre y tasa de la variante como foto: si el producto cambia o se borra después,
/// la orden sigue mostrando lo que se pidió. Los valores confirmados quedan nulos hasta que el proveedor confirma.
/// </summary>
public sealed class PurchaseOrderLine
{
    // EF Core materializa por acá; el agregado crea las líneas con From.
    private PurchaseOrderLine()
    {
        Code = string.Empty;
        Name = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>FK blanda a la variante del catálogo: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid VariantId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>Costo unitario sin IVA.</summary>
    public decimal UnitCost { get; private set; }

    public decimal? ConfirmedQuantity { get; private set; }

    public decimal? ConfirmedUnitCost { get; private set; }

    public decimal TaxRatePercent { get; private set; }

    public PurchaseOrderLineStatus Status => ConfirmedQuantity switch
    {
        null => PurchaseOrderLineStatus.Pending,
        0m => PurchaseOrderLineStatus.Rejected,
        var quantity when quantity == Quantity && ConfirmedUnitCost == UnitCost => PurchaseOrderLineStatus.Unchanged,
        _ => PurchaseOrderLineStatus.Changed,
    };

    /// <summary>
    /// Subtotal efectivo sin IVA (D-5): con los valores confirmados si la línea ya se confirmó, aunque la orden se haya
    /// cancelado después; si no, con los pedidos.
    /// </summary>
    public decimal Subtotal => ConfirmedQuantity is { } quantity
        ? PurchaseOrderMath.Subtotal(quantity, ConfirmedUnitCost ?? UnitCost)
        : RequestedSubtotal;

    /// <summary>Subtotal sin IVA con lo pedido, sin importar lo confirmado.</summary>
    public decimal RequestedSubtotal => PurchaseOrderMath.Subtotal(Quantity, UnitCost);

    public decimal Tax => PurchaseOrderMath.Tax(Subtotal, TaxRatePercent);

    public decimal RequestedTax => PurchaseOrderMath.Tax(RequestedSubtotal, TaxRatePercent);

    internal static PurchaseOrderLine From(PurchaseOrderLineDraft draft) => new()
    {
        Id = Guid.CreateVersion7(),
        VariantId = draft.VariantId,
        Code = draft.Code,
        Name = draft.Name,
        Quantity = draft.Quantity,
        UnitCost = draft.UnitCost,
        TaxRatePercent = draft.TaxRatePercent,
    };

    internal void Confirm(decimal quantity, decimal unitCost)
    {
        ConfirmedQuantity = quantity;
        ConfirmedUnitCost = unitCost;
    }
}
