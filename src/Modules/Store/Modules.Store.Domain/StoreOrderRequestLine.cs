namespace Modules.Store.Domain;

/// <summary>Una línea tal como la cotizó el motor de pedidos al recibir la solicitud; el origen del descuento viaja como texto porque el dominio no conoce a Quotations.</summary>
public sealed record StoreOrderRequestLineInput(
    Guid VariantId,
    Guid ProductId,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    string DiscountOrigin,
    decimal DiscountedUnitPrice,
    decimal LineTotal);

public sealed class StoreOrderRequestLine
{
    public const int NameMaxLength = 200;

    public const int DiscountOriginMaxLength = 16;

    // EF Core materializa por acá.
    private StoreOrderRequestLine()
    {
        Name = string.Empty;
        DiscountOrigin = string.Empty;
    }

    public int Position { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountPercentage { get; private set; }

    public string DiscountOrigin { get; private set; }

    public decimal DiscountedUnitPrice { get; private set; }

    public decimal LineTotal { get; private set; }

    internal static StoreOrderRequestLine From(int position, StoreOrderRequestLineInput input)
    {
        if (input.Quantity <= 0m)
        {
            throw new StoreDomainException(StoreErrorCodes.ValidationFailed, "La cantidad de una línea debe ser positiva.");
        }

        return new StoreOrderRequestLine
        {
            Position = position,
            VariantId = input.VariantId,
            ProductId = input.ProductId,
            Name = StoreText.Required(input.Name, NameMaxLength, "El nombre de la línea"),
            Quantity = input.Quantity,
            UnitPrice = input.UnitPrice,
            DiscountPercentage = input.DiscountPercentage,
            DiscountOrigin = StoreText.Optional(input.DiscountOrigin, DiscountOriginMaxLength, "El origen del descuento") ?? string.Empty,
            DiscountedUnitPrice = input.DiscountedUnitPrice,
            LineTotal = input.LineTotal,
        };
    }
}
