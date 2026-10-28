namespace Modules.Catalog.Domain;

/// <summary>
/// Id de una <see cref="ProductVariant"/>. La variante por defecto de un producto lleva el mismo
/// <see cref="Guid"/> que el producto (<see cref="Product.Create"/>): así la migración rellena
/// <c>variant_id</c> en las líneas de Quotations y Pos sin consultar entre esquemas.
/// </summary>
public readonly record struct VariantId(Guid Value)
{
    public static VariantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
