namespace Modules.Purchases.Domain;

/// <summary>
/// Una línea por guardar: la variante y lo que de ella se copia del catálogo (código, nombre y tasa de IVA, como foto),
/// más la cantidad y el costo sin IVA pedidos. Application la arma; el dominio no mira el catálogo. Una orden externa trae
/// <see cref="VariantId"/> y no <see cref="Listing"/>; una vinculada al revés: la variante es del mayorista y la del
/// catálogo propio se resuelve al confirmar.
/// </summary>
public sealed record PurchaseOrderLineDraft(
    Guid? VariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitCost,
    decimal TaxRatePercent,
    LinkedLinePrice? Listing = null);

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

    /// <summary>
    /// FK blanda a la variante del catálogo propio: Guid y no un id tipado, como en el resto de módulos. Nula en una línea
    /// vinculada hasta que la confirmación la encuentra o la crea (<see cref="PurchaseOrder.LinkLineToCatalog"/>).
    /// </summary>
    public Guid? VariantId { get; private set; }

    /// <summary>Variante del catálogo del mayorista; sólo en órdenes vinculadas.</summary>
    public Guid? SupplierVariantId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    /// <summary>Cantidad pedida. Nula sólo en una línea que agregó el mayorista al aprobar (<c>Added</c>).</summary>
    public decimal? Quantity { get; private set; }

    /// <summary>
    /// Costo unitario sin IVA. Nulo en una línea vinculada que nació sin precio (lista oculta, spec 2026-11-19): el
    /// distribuidor nunca pidió a un precio, y los costos llegan con la aprobación (<see cref="ConfirmedUnitCost"/>).
    /// </summary>
    public decimal? UnitCost { get; private set; }

    public decimal? ConfirmedQuantity { get; private set; }

    public decimal? ConfirmedUnitCost { get; private set; }

    public decimal TaxRatePercent { get; private set; }

    /// <summary>Precio base de la lista del mayorista, IVA incluido (D-9); sólo en órdenes vinculadas.</summary>
    public decimal? ListBasePrice { get; private set; }

    /// <summary>Descuento del distribuidor sobre <see cref="ListBasePrice"/>, en porcentaje.</summary>
    public decimal? ListDiscountPercent { get; private set; }

    /// <summary>Precio de lista con el descuento, IVA incluido: de él sale <see cref="UnitCost"/>.</summary>
    public decimal? ListUnitPrice { get; private set; }

    /// <summary>True si la confirmación creó el producto en el catálogo propio en vez de encontrarlo.</summary>
    public bool CreatedInCatalog { get; private set; }

    // D-12: sin precio pedido sólo se compara la cantidad; no hay un precio que haya «cambiado».
    public PurchaseOrderLineStatus Status => (Quantity, ConfirmedQuantity) switch
    {
        (null, _) => PurchaseOrderLineStatus.Added,
        (_, null) => PurchaseOrderLineStatus.Pending,
        (_, 0m) => PurchaseOrderLineStatus.Rejected,
        var (requested, confirmed) when confirmed == requested && (UnitCost is null || ConfirmedUnitCost == UnitCost) =>
            PurchaseOrderLineStatus.Unchanged,
        _ => PurchaseOrderLineStatus.Changed,
    };

    /// <summary>
    /// Subtotal efectivo sin IVA (D-5): con los valores confirmados si la línea ya se confirmó, aunque la orden se haya
    /// cancelado después; si no, con los pedidos. Nulo mientras no haya un costo con qué calcularlo (línea sin precio que
    /// el mayorista todavía no aprobó); una línea rechazada (cantidad confirmada 0) vale 0.
    /// </summary>
    public decimal? Subtotal => ConfirmedQuantity switch
    {
        0m => 0m,
        { } quantity => (ConfirmedUnitCost ?? UnitCost) is { } cost ? PurchaseOrderMath.Subtotal(quantity, cost) : null,
        null => RequestedSubtotal,
    };

    /// <summary>Subtotal sin IVA con lo pedido, sin importar lo confirmado. Una línea <c>Added</c> no se pidió: suma 0.</summary>
    public decimal? RequestedSubtotal => UnitCost is { } cost ? PurchaseOrderMath.Subtotal(Quantity ?? 0m, cost) : null;

    public decimal? Tax => Subtotal is { } subtotal ? PurchaseOrderMath.Tax(subtotal, TaxRatePercent) : null;

    public decimal? RequestedTax =>
        RequestedSubtotal is { } subtotal ? PurchaseOrderMath.Tax(subtotal, TaxRatePercent) : null;

    /// <summary>
    /// <paramref name="withPrices"/> en <c>false</c> arma la línea sin ningún monto (costo ni lista del mayorista): es el
    /// único punto donde se descartan, para que ni el adaptador ni el DTO tengan que enmascarar nada.
    /// </summary>
    internal static PurchaseOrderLine From(PurchaseOrderLineDraft draft, bool withPrices) => new()
    {
        Id = Guid.CreateVersion7(),
        VariantId = draft.VariantId,
        SupplierVariantId = draft.Listing?.SupplierVariantId,
        Code = draft.Code,
        Name = draft.Name,
        Quantity = draft.Quantity,
        UnitCost = withPrices ? draft.UnitCost : null,
        TaxRatePercent = draft.TaxRatePercent,
        ListBasePrice = withPrices ? draft.Listing?.BasePrice : null,
        ListDiscountPercent = withPrices ? draft.Listing?.DiscountPercent : null,
        ListUnitPrice = withPrices ? draft.Listing?.UnitPrice : null,
    };

    internal static PurchaseOrderLine From(LinkedLineSnapshot snapshot, bool withPrices) => new()
    {
        Id = snapshot.LineId,
        SupplierVariantId = snapshot.SupplierVariantId,
        Code = snapshot.Code,
        Name = snapshot.Name,
        Quantity = snapshot.Quantity,
        UnitCost = withPrices ? snapshot.UnitCost : null,
        TaxRatePercent = snapshot.TaxRatePercent,
        ListBasePrice = withPrices ? snapshot.BasePrice : null,
        ListDiscountPercent = withPrices ? snapshot.DiscountPercent : null,
        ListUnitPrice = withPrices ? snapshot.UnitPrice : null,
    };

    /// <summary>Una línea que el mayorista agregó al editar el pedido: no se pidió, así que llega ya confirmada.</summary>
    internal static PurchaseOrderLine Added(WholesalerFinalLine line) => new()
    {
        Id = Guid.CreateVersion7(),
        SupplierVariantId = line.SupplierVariantId,
        Code = line.Code,
        Name = line.Name,
        Quantity = null,
        UnitCost = line.UnitCost,
        TaxRatePercent = line.TaxRatePercent,
        ConfirmedQuantity = line.Quantity,
        ConfirmedUnitCost = line.UnitCost,
    };

    internal void Confirm(decimal quantity, decimal? unitCost)
    {
        ConfirmedQuantity = quantity;
        ConfirmedUnitCost = unitCost;
    }

    internal void LinkToCatalog(Guid variantId, bool created)
    {
        VariantId = variantId;
        CreatedInCatalog = created;
    }
}
