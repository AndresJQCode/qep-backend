namespace Modules.Purchases.Domain;

/// <summary>
/// La foto de la lista del mayorista para una línea de una orden vinculada (D-9): precio base, descuento del distribuidor y
/// precio de lista resultante, todos con IVA incluido. Sólo sirven para pintar «base y descuento»; el costo de la línea es
/// el precio de lista sin IVA (<see cref="PurchaseOrderMath.NetOfVat"/>).
/// </summary>
public sealed record LinkedLinePrice(Guid SupplierVariantId, decimal BasePrice, decimal DiscountPercent, decimal UnitPrice);

/// <summary>
/// Una línea tal como quedó en el pedido del mayorista al enviar (D-8): la orden adopta su id para que la aprobación,
/// que vuelve con esos ids, se reconcilie línea por línea.
/// </summary>
public sealed record LinkedLineSnapshot(
    Guid LineId,
    Guid SupplierVariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitCost,
    decimal TaxRatePercent,
    decimal BasePrice,
    decimal DiscountPercent,
    decimal UnitPrice);

/// <summary>
/// Una línea del pedido que aprobó el mayorista, con el costo ya sin IVA. <see cref="SourceLineId"/> es el id de la línea
/// de la orden de la que salió; nulo si el mayorista la agregó al editar el pedido.
/// </summary>
public sealed record WholesalerFinalLine(
    Guid? SourceLineId,
    Guid SupplierVariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitCost,
    decimal TaxRatePercent);
