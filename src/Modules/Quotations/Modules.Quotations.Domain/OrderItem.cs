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
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int position,
        string currency,
        DateTimeOffset occurredAt)
    {
        Id = id;
        OrderId = orderId;
        ProductId = productId;
        Position = position;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Apply(quantity, unitPrice, discountPercentage, taxPercentage, currency, occurredAt);
    }

    public OrderItemId Id { get; private set; }

    public OrderId OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string? ProductCode { get; private set; }

    public string? ProductName { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercentage { get; private set; }

    public QuotationDiscountOrigin DiscountOrigin { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal Subtotal { get; private set; }

    public int TaxPercentage { get; private set; }

    public decimal TaxAmount { get; private set; }

    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public decimal DiscountedUnitPrice =>
        Quantity > 0 ? Round((Round(Quantity * UnitPrice) - DiscountAmount) / Quantity) : 0m;

    public decimal DiscountedUnitPriceWithoutTax => Quantity > 0 ? Round(Subtotal / Quantity) : 0m;

    public decimal UnitPriceWithoutTax => Round(UnitPrice * 100m / (100m + TaxPercentage));

    /// <summary>Lo que la línea aporta a <see cref="DocumentTotals"/>.</summary>
    public LineAmountsView Amounts => new(Subtotal, DiscountAmount, TaxAmount);

    internal static OrderItem Create(
        OrderItemId id,
        OrderId orderId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int position,
        string currency,
        DateTimeOffset occurredAt) =>
        new(id, orderId, productId, quantity, unitPrice, discountPercentage, taxPercentage, position, currency, occurredAt);

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
        int taxPercentage,
        string currency,
        DateTimeOffset occurredAt) =>
        Apply(quantity, UnitPrice, discountPercentage, taxPercentage, currency, occurredAt);

    internal void ApplyDiscount(
        decimal discountPercentage,
        QuotationDiscountOrigin origin,
        string currency,
        DateTimeOffset occurredAt)
    {
        DiscountOrigin = origin;
        Apply(Quantity, UnitPrice, discountPercentage, TaxPercentage, currency, occurredAt);
    }

    private void Apply(
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        string currency,
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

        // Mismas dos reglas que QuotationItem.Apply: pesos enteros en COP y sin IVA discriminado
        // fuera de COP.
        unitPrice = VatIncludedLine.ChargedListPrice(unitPrice, currency);
        if (!QuotationItem.ItemizesTaxIn(currency))
        {
            taxPercentage = 0;
        }

        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountPercentage = discountPercentage;
        TaxPercentage = taxPercentage;
        var amounts = VatIncludedLine.Compute(quantity, unitPrice, discountPercentage, taxPercentage, currency);
        DiscountAmount = amounts.DiscountAmount;
        TaxAmount = amounts.TaxAmount;
        Subtotal = amounts.Subtotal;
        UpdatedAt = occurredAt;
    }

    private static decimal Round(decimal value) => VatIncludedLine.Round(value);
}
