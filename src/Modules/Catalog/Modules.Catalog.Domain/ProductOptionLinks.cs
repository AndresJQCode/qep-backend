namespace Modules.Catalog.Domain;

/// <summary>
/// Una opción de la biblioteca que usa un producto, en su posición (fila de
/// <c>catalog.product_options</c>). Existe para que la persistencia tenga una fila por opción con
/// FK a <c>option_definitions</c>: quien lee el producto ve <see cref="Product.OptionIds"/>.
/// Se reemplaza en el lugar, por posición, para que EF no siga una fila borrada y otra agregada con
/// la misma clave en el mismo guardado (mismo motivo que <c>ProductVariant.ReplacePrices</c>).
/// </summary>
public sealed class ProductOption
{
    // EF Core materializa por acá.
    private ProductOption() { }

    internal ProductOption(OptionDefinitionId optionDefinitionId, int position)
    {
        OptionDefinitionId = optionDefinitionId;
        Position = position;
    }

    public OptionDefinitionId OptionDefinitionId { get; internal set; }

    /// <summary>Base 0; el orden de la opción en el producto y en el nombre visible.</summary>
    public int Position { get; private set; }
}

/// <summary>
/// El valor que una variante tiene para la opción de su misma posición (fila de
/// <c>catalog.product_variant_option_values</c>). Mismo criterio que <see cref="ProductOption"/>:
/// quien lee la variante ve <see cref="ProductVariant.OptionValueIds"/>.
/// </summary>
public sealed class ProductVariantOptionValue
{
    // EF Core materializa por acá.
    private ProductVariantOptionValue() { }

    internal ProductVariantOptionValue(Guid optionValueId, int position)
    {
        OptionValueId = optionValueId;
        Position = position;
    }

    public Guid OptionValueId { get; internal set; }

    /// <summary>Base 0; la misma posición que la opción del producto a la que responde.</summary>
    public int Position { get; private set; }
}
