namespace Modules.Quotations.Domain;

/// <summary>
/// Una línea de producto de una cotización (modelo-datos-cotizaciones.md §2.2). Entidad hija de
/// <see cref="Quotation"/> — no tiene repositorio propio ni se crea fuera de él, mismo criterio
/// que <c>PriceScale</c> dentro de <c>Product</c> en Catalog.
/// </summary>
public sealed class QuotationItem
{
    // EF Core materializa por acá. El código nunca construye la entidad así: sólo
    // Quotation.AddItem hace cumplir los invariantes.
    private QuotationItem()
    {
    }

    private QuotationItem(
        QuotationItemId id,
        QuotationId quotationId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int position,
        DateTimeOffset occurredAt)
    {
        Id = id;
        QuotationId = quotationId;
        ProductId = productId;
        Position = position;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        Apply(quantity, unitPrice, discountPercentage, taxPercentage, occurredAt);
    }

    public QuotationItemId Id { get; private set; }

    public QuotationId QuotationId { get; private set; }

    /// <summary>Referencia blanda al catálogo de productos (módulo Catalog). Sin FK a propósito:
    /// ningún módulo de negocio referencia las tablas de otro.</summary>
    public Guid ProductId { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>Precio base del producto, snapshot al momento de agregar la línea. No se
    /// recalcula si el precio del producto cambia después (modelo-datos-cotizaciones.md
    /// §1.8): una cotización es un documento histórico.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>Calculado por la aplicación según la escala de precios del producto para
    /// <see cref="Quantity"/> — nunca editable a mano (decisión confirmada).</summary>
    public decimal DiscountPercentage { get; private set; }

    public decimal DiscountAmount { get; private set; }

    /// <summary>(Quantity × UnitPrice) − DiscountAmount. Ya neto del descuento de la línea.</summary>
    public decimal Subtotal { get; private set; }

    /// <summary>Tasa de impuesto del producto (<c>Catalog.TaxRate.Percentage</c>), snapshot al
    /// momento de agregar/editar la línea — mismo criterio que <see cref="UnitPrice"/>: una
    /// cotización es un documento histórico, no sigue la tasa del producto si ésta cambia
    /// después. 0 si el producto no tiene tasa de impuesto asignada.</summary>
    public int TaxPercentage { get; private set; }

    /// <summary>Subtotal × TaxPercentage / 100. El impuesto de la cotización es la suma de este
    /// campo en todas sus líneas (<see cref="Quotation.RecalculateTotals"/>), no un porcentaje
    /// único aplicado al subtotal completo.</summary>
    public decimal TaxAmount { get; private set; }

    /// <summary>Posición de la fila para mantener el orden de despliegue.</summary>
    public int Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static QuotationItem Create(
        QuotationItemId id,
        QuotationId quotationId,
        Guid productId,
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        int position,
        DateTimeOffset occurredAt) =>
        new(id, quotationId, productId, quantity, unitPrice, discountPercentage, taxPercentage, position, occurredAt);

    internal void UpdateQuantity(
        decimal quantity, decimal discountPercentage, int taxPercentage, DateTimeOffset occurredAt) =>
        Apply(quantity, UnitPrice, discountPercentage, taxPercentage, occurredAt);

    private void Apply(
        decimal quantity,
        decimal unitPrice,
        decimal discountPercentage,
        int taxPercentage,
        DateTimeOffset occurredAt)
    {
        if (quantity <= 0)
        {
            throw new QuotationsDomainException(
                "quotation.item.quantity_invalid",
                "The item quantity must be greater than zero.");
        }

        if (unitPrice < 0)
        {
            throw new QuotationsDomainException(
                "quotation.item.unit_price_negative",
                "The item unit price cannot be negative.");
        }

        if (discountPercentage < 0 || discountPercentage > 100)
        {
            throw new QuotationsDomainException(
                "quotation.item.discount_out_of_range",
                "The item discount percentage must be between 0 and 100.");
        }

        if (taxPercentage < 0 || taxPercentage > 100)
        {
            throw new QuotationsDomainException(
                "quotation.item.tax_percentage_out_of_range",
                "The item tax percentage must be between 0 and 100.");
        }

        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountPercentage = discountPercentage;
        TaxPercentage = taxPercentage;

        var gross = quantity * unitPrice;
        DiscountAmount = Round(gross * discountPercentage / 100m);
        Subtotal = Round(gross) - DiscountAmount;
        TaxAmount = Round(Subtotal * taxPercentage / 100m);
        UpdatedAt = occurredAt;
    }

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
