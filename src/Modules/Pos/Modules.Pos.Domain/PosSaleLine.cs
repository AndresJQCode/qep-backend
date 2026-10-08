using BuildingBlocks.Domain.Pricing;

namespace Modules.Pos.Domain;

public sealed class PosSaleLine
{
    private PosSaleLine()
    {
        ProductCode = string.Empty;
        ProductName = string.Empty;
    }

    public PosSaleLineId Id { get; private set; }

    public PosSaleId SaleId { get; private set; }

    public int Position { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductCode { get; private set; }

    public string ProductName { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercentage { get; private set; }

    public int TaxPercentage { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal Subtotal { get; private set; }

    /// <summary>Derivado y no persistido, como el precio con descuento de Quotations.</summary>
    public decimal LineTotal => Subtotal + TaxAmount;

    internal static PosSaleLine Create(PosSaleId saleId, int position, PosSaleLineInput input)
    {
        if (input.Quantity <= 0 || input.Quantity > PosLimits.MaxQuantity || !PosLimits.HasValidScale(input.Quantity))
        {
            throw new PosDomainException(
                "pos.sale.quantity_invalid",
                "The quantity must be greater than 0, at most 99999 and have at most 2 decimals.");
        }

        if (input.DiscountPercentage < 0 || input.DiscountPercentage > 100 || !PosLimits.HasValidScale(input.DiscountPercentage))
        {
            throw new PosDomainException(
                "pos.sale.discount_out_of_range",
                "The discount must be between 0 and 100 with at most 2 decimals.");
        }

        // Precio y tasa vienen del catálogo, no del cliente: fuera de rango es un bug.
        ArgumentOutOfRangeException.ThrowIfNegative(input.UnitPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(input.TaxPercentage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.TaxPercentage, 100);

        var amounts = VatIncludedLine.Compute(
            input.Quantity, input.UnitPrice, input.DiscountPercentage, input.TaxPercentage);

        return new PosSaleLine
        {
            Id = PosSaleLineId.New(),
            SaleId = saleId,
            Position = position,
            ProductId = input.ProductId,
            ProductCode = input.ProductCode,
            ProductName = input.ProductName,
            Quantity = input.Quantity,
            UnitPrice = input.UnitPrice,
            DiscountPercentage = input.DiscountPercentage,
            TaxPercentage = input.TaxPercentage,
            DiscountAmount = amounts.DiscountAmount,
            TaxAmount = amounts.TaxAmount,
            Subtotal = amounts.Subtotal,
        };
    }
}
