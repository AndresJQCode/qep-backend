namespace Modules.Catalog.Domain;

/// <summary>
/// Qué precio cambió en una fila de <see cref="ProductPriceChange"/>. Son los tres únicos
/// valores que el histórico sigue: los dos precios base del producto y el descuento de una
/// escala.
///
/// Los precios finales de la escala no están: se derivan de la base y el descuento
/// —<c>PriceScale.ValidateFinal</c> lo hace cumplir—, así que guardarlos sería registrar dos
/// veces el mismo cambio.
/// </summary>
public enum ProductPriceField
{
    PriceBaseUsd,
    PriceBaseCop,
    ScaleDiscount
}
