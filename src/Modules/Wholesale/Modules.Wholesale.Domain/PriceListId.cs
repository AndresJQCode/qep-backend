namespace Modules.Wholesale.Domain;

/// <summary>
/// El id de la lista de precios de un distribuidor. Desde el spec 2026-11-22 la lista vive en Catalog (<c>catalog.price_lists</c>):
/// esto es una referencia blanda, sin FK (otro esquema), y una lista borrada deja al distribuidor sin lista.
/// </summary>
public readonly record struct PriceListId(Guid Value)
{
    public static PriceListId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
