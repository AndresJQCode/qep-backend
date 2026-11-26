using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Pricing;

namespace Modules.Quotations.Domain;

/// <summary>
/// Una línea del pedido, con la misma aritmética que <see cref="QuotationItem"/> (spec 2026-10-15 §4.3):
/// precio unitario con IVA, descuento como porcentaje, tasa del producto, foto de código y nombre. Sólo
/// <see cref="Order"/> la construye y la muta.
/// </summary>
public sealed class OrderItem
{
    // EF Core materializa por acá. El código nunca construye la entidad así: sólo Order hace cumplir
    // los invariantes.
    private OrderItem()
    {
    }

    private OrderItem(
        OrderItemId id,
        OrderId orderId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int productTaxPercentage,
        bool chargesVat,
        int position,
        string currency,
        PriceRounding rounding,
        ProductKind kind,
        SalesUnit salesUnit,
        DateTimeOffset occurredAt,
        bool isGift)
    {
        Id = id;
        OrderId = orderId;
        VariantId = variantId;
        ProductId = productId;
        Kind = kind;
        SalesUnit = salesUnit;
        Position = position;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        IsGift = isGift;
        Apply(
            quantity, unitPrice, isGift ? GiftDiscountPercentage : discountPercentage, productTaxPercentage, chargesVat,
            currency, rounding, occurredAt);
    }

    /// <summary>El descuento fijo de una línea de regalo (spec 2026-11-21): el precio real se conserva y el cliente no paga.</summary>
    public const decimal GiftDiscountPercentage = 100m;

    public OrderItemId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    /// <summary>La variante que vende la línea: la clave de la línea, igual que en
    /// <see cref="QuotationItem.VariantId"/>.</summary>
    public Guid VariantId { get; private set; }

    /// <summary>El producto dueño de <see cref="VariantId"/>, para agrupar por producto.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Producto o servicio, fijado al crear la línea (spec 2026-11-18 §3.4), igual que
    /// <see cref="QuotationItem.Kind"/>. Al convertir se copia el de la línea de la cotización, no el que el producto
    /// tenga hoy; los pedidos de Shopify nacen <c>Physical</c>.</summary>
    public ProductKind Kind { get; private set; }

    /// <summary>La unidad de venta de la línea, con la misma regla que <see cref="Kind"/>.</summary>
    public SalesUnit SalesUnit { get; private set; }

    /// <summary>Si la línea es un regalo (spec 2026-11-21): descuento fijo en 100 %, precio real conservado. La clave de la
    /// línea es (<see cref="VariantId"/>, <see cref="IsGift"/>): puede haber una pagada y una de regalo de la misma variante.</summary>
    public bool IsGift { get; private set; }

    public string? ProductCode { get; private set; }

    /// <summary>La línea de la orden de compra vinculada de la que salió (mayorista fase 3). Null en cualquier otro pedido y
    /// en una línea que el mayorista agregó al editar.</summary>
    public Guid? SourcePurchaseOrderLineId { get; private set; }

    public string? ProductName { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercentage { get; private set; }

    public QuotationDiscountOrigin DiscountOrigin { get; private set; }

    /// <summary>El descuento de la línea en base sin IVA, como <see cref="Subtotal"/> (spec 2026-11-23).</summary>
    public decimal DiscountAmount { get; private set; }

    public decimal Subtotal { get; private set; }

    /// <summary>La tasa efectiva (spec 2026-11-17, R1): 0 fuera de COP y cuando la empresa emisora no cobra IVA.</summary>
    public int TaxPercentage { get; private set; }

    /// <summary>La tasa del producto al tomar la foto (R1), igual que <see cref="QuotationItem.ProductTaxPercentage"/>.
    /// Null en una fila anterior a la columna.</summary>
    public int? ProductTaxPercentage { get; private set; }

    /// <summary>La tasa del producto, con el respaldo de las filas viejas (R1).</summary>
    public int ProductTaxRate => ProductTaxPercentage ?? TaxPercentage;

    public decimal TaxAmount { get; private set; }

    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// El costo unitario de la variante cuando la línea contó como vendida (spec 2026-11-10 §4.2), en la moneda
    /// del pedido. Null con <see cref="CostCapturedAt"/> lleno es «se congeló sin costo»; con él vacío, «todavía
    /// no se congeló». Nunca se expone por la API de pedidos.
    /// </summary>
    public decimal? UnitCost { get; private set; }

    public LineCostSource? CostSource { get; private set; }

    public DateTimeOffset? CostCapturedAt { get; private set; }

    /// <summary>Congela el costo una sola vez (§4.3): una línea ya congelada no se reescribe, aunque el pedido
    /// salga de pagado y vuelva, o el costo del catálogo cambie.</summary>
    internal void FreezeCost(decimal? unitCost, DateTimeOffset at)
    {
        if (CostCapturedAt is not null)
        {
            return;
        }

        UnitCost = unitCost;
        CostSource = LineCostSource.Manual;
        CostCapturedAt = at;
    }

    /// <summary>El unitario de la fórmula con la regla del documento (<see cref="Order.PriceRounding"/>),
    /// la misma con que se cobró la línea. Derivado, no persistido.</summary>
    public decimal DiscountedUnitPriceUnder(PriceRounding rounding) =>
        Quantity > 0 ? VatIncludedLine.ChargedUnitPrice(UnitPrice, DiscountPercentage, rounding) : 0m;

    public decimal DiscountedUnitPriceWithoutTax => Quantity > 0 ? Round(Subtotal / Quantity) : 0m;

    public decimal UnitPriceWithoutTax => Round(UnitPrice * 100m / (100m + TaxPercentage));

    /// <summary>Lo que la línea aporta a <see cref="DocumentTotals"/>; el tipo decide su base de retención.</summary>
    public LineAmountsView Amounts => new(Subtotal, DiscountAmount, TaxAmount, Kind);

    internal static OrderItem Create(
        OrderItemId id,
        OrderId orderId,
        Guid variantId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int productTaxPercentage,
        bool chargesVat,
        int position,
        string currency,
        PriceRounding rounding,
        ProductKind kind,
        SalesUnit salesUnit,
        DateTimeOffset occurredAt,
        bool isGift = false) =>
        new(
            id, orderId, variantId, productId, quantity, unitPrice, discountPercentage, productTaxPercentage, chargesVat,
            position, currency, rounding, kind, salesUnit, occurredAt, isGift);

    /// <summary>Renumera la línea tras reemplazar las de una edición externa, para que dos no compartan posición.</summary>
    internal void MoveTo(int position) => Position = position;

    internal void LinkPurchaseOrderLine(Guid? purchaseOrderLineId) => SourcePurchaseOrderLineId = purchaseOrderLineId;

    /// <summary>D-4: el precio de lista de un pedido mayorista se corrige a mano. Reprecia con la cantidad, el descuento y la
    /// tasa del producto que la línea ya tiene, igual que <see cref="UpdateQuantity"/>.</summary>
    internal void ChangeUnitPrice(
        decimal unitPrice,
        bool chargesVat,
        string currency,
        PriceRounding rounding,
        DateTimeOffset occurredAt) =>
        Apply(Quantity, unitPrice, DiscountPercentage, ProductTaxRate, chargesVat, currency, rounding, occurredAt);

    internal void CaptureProduct(QuotationProductSnapshot product)
    {
        if (ProductCode is not null)
        {
            return;
        }

        ProductCode = product.Code;
        ProductName = product.Name;
    }

    internal void UpdateQuantity(
        decimal quantity,
        decimal discountPercentage,
        int productTaxPercentage,
        bool chargesVat,
        string currency,
        PriceRounding rounding,
        DateTimeOffset occurredAt) =>
        // Un regalo conserva su 100 %: sólo la cantidad se edita.
        Apply(
            quantity, UnitPrice, IsGift ? GiftDiscountPercentage : discountPercentage, productTaxPercentage, chargesVat,
            currency, rounding, occurredAt);

    /// <summary>Spec 2026-11-14 §4: la línea que una edición de la plataforma conserva, con el precio y el descuento que la
    /// plataforma cobró ahora (sin repreciar contra el catálogo). Conserva su id y su costo congelado.</summary>
    internal void ApplyExternal(
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int productTaxPercentage,
        bool chargesVat,
        int position,
        string currency,
        PriceRounding rounding,
        DateTimeOffset occurredAt)
    {
        DiscountOrigin = QuotationDiscountOrigin.Own;
        Position = position;
        Apply(quantity, unitPrice, discountPercentage, productTaxPercentage, chargesVat, currency, rounding, occurredAt);
    }

    /// <summary>Rehace la línea con el IVA de la empresa emisora (spec 2026-11-17, R2) desde la tasa del producto.</summary>
    internal void ApplyChargesVat(bool chargesVat, string currency, PriceRounding rounding, DateTimeOffset occurredAt) =>
        Apply(Quantity, UnitPrice, DiscountPercentage, ProductTaxRate, chargesVat, currency, rounding, occurredAt);

    /// <summary>La misma línea, comparada como se guarda: precio redondeado con la regla del documento y sin IVA fuera de
    /// COP. Así una edición que llega igual a lo que el pedido ya tiene no cuenta como cambio.</summary>
    internal bool SameAs(OrderItem other) =>
        VariantId == other.VariantId
        && IsGift == other.IsGift
        && ProductId == other.ProductId
        && Quantity == other.Quantity
        && UnitPrice == other.UnitPrice
        && DiscountPercentage == other.DiscountPercentage
        && TaxPercentage == other.TaxPercentage;

    internal void ApplyDiscount(
        decimal discountPercentage,
        QuotationDiscountOrigin origin,
        string currency,
        PriceRounding rounding,
        DateTimeOffset occurredAt)
    {
        DiscountOrigin = origin;
        if (IsGift)
        {
            discountPercentage = GiftDiscountPercentage;
        }

        // La tasa efectiva se conserva tal cual: el descuento no cambia si la línea lleva IVA.
        Compute(Quantity, UnitPrice, discountPercentage, TaxPercentage, ProductTaxRate, rounding, occurredAt);
    }

    // Misma regla que QuotationItem.Apply: la tasa efectiva es la del producto sólo si la empresa cobra IVA y la moneda
    // lo discrimina (spec 2026-11-17, R1).
    private void Apply(
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int productTaxPercentage,
        bool chargesVat,
        string currency,
        PriceRounding rounding,
        DateTimeOffset occurredAt)
    {
        if (productTaxPercentage < 0 || productTaxPercentage > 100)
        {
            throw new QuotationsDomainException(
                "order.item.tax_percentage_out_of_range",
                "The item tax percentage must be between 0 and 100.");
        }

        var effective = chargesVat && QuotationItem.ItemizesTaxIn(currency) ? productTaxPercentage : 0;
        Compute(quantity, unitPrice, discountPercentage, effective, productTaxPercentage, rounding, occurredAt);
    }

    private void Compute(
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int productTaxPercentage,
        PriceRounding rounding,
        DateTimeOffset occurredAt)
    {
        if (quantity <= 0)
        {
            throw new QuotationsDomainException(
                "order.item.quantity_invalid",
                "The item quantity must be greater than zero.");
        }

        if (unitPrice < 0)
        {
            throw new QuotationsDomainException(
                "order.item.unit_price_negative",
                "The item unit price cannot be negative.");
        }

        if (discountPercentage < 0 || discountPercentage > 100)
        {
            throw new QuotationsDomainException(
                "order.item.discount_out_of_range",
                "The item discount percentage must be between 0 and 100.");
        }

        if (taxPercentage < 0 || taxPercentage > 100)
        {
            throw new QuotationsDomainException(
                "order.item.tax_percentage_out_of_range",
                "The item tax percentage must be between 0 and 100.");
        }

        // Pesos enteros en COP, la misma regla que QuotationItem.
        unitPrice = VatIncludedLine.ChargedListPrice(unitPrice, rounding);

        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountPercentage = discountPercentage;
        TaxPercentage = taxPercentage;
        ProductTaxPercentage = productTaxPercentage;
        var amounts = VatIncludedLine.Compute(quantity, unitPrice, discountPercentage, taxPercentage, rounding);
        // Sin IVA, como Subtotal (spec 2026-11-23): bruto sin IVA − descuento = Subtotal.
        DiscountAmount = amounts.DiscountAmountWithoutTax;
        TaxAmount = amounts.TaxAmount;
        Subtotal = amounts.Subtotal;
        UpdatedAt = occurredAt;
    }

    private static decimal Round(decimal value) => VatIncludedLine.Round(value);
}
