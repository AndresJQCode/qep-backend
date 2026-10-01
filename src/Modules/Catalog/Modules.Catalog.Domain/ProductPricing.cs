namespace Modules.Catalog.Domain;

/// <summary>
/// Lo que un POST y un PUT de producto mandan para fijar precio y escalas. Agrupado por la
/// misma razón que <see cref="ProductDetails"/>: la cantidad de invariantes que cruzan estos
/// campos (moneda-por-moneda, base y final, descuento) no tendría dónde vivir suelta.
/// </summary>
public sealed record ProductPricing
{
    public decimal? BaseUsd { get; init; }

    public decimal? BaseCop { get; init; }

    public IReadOnlyCollection<PriceScaleInput> Scales { get; init; } = [];

    /// <summary>
    /// Los empaques en que viene el producto (p. ej. cajas de 100 y de 150). Viajan acá, junto a
    /// las escalas, y no en <see cref="ProductDetails"/>: una escala con restricción
    /// <see cref="PriceScaleRestriction.PackagingUnit"/> se valida contra este conjunto, así que
    /// los dos se validan juntos en el mismo POST/PUT. Ver <see cref="Product.PackagingUnits"/>.
    /// </summary>
    public IReadOnlyCollection<int> PackagingUnits { get; init; } = [];
}

/// <summary>
/// Una escala de precio tal como la manda el cliente, sin id: el conjunto completo se
/// reemplaza en cada `PUT`, así que <see cref="Product"/> asigna un <see cref="PriceScaleId"/>
/// nuevo a cada una — el mismo criterio que ya usa <c>ProductDetails</c> para sus cinco
/// opcionales.
///
/// No lleva unidad de empaque: desde el 2026-10-01 el empaque es del producto
/// (<see cref="ProductPricing.PackagingUnits"/>), y una escala con restricción
/// <see cref="PriceScaleRestriction.PackagingUnit"/> sólo dice "usa los empaques del producto".
/// </summary>
/// <param name="AllowGrouping">Si las cantidades de varias líneas de una cotización que caen en
/// esta misma escala se suman para validar el múltiplo. Exclusivo de
/// <see cref="PriceScaleRestriction.Multiple"/>. Último y con default a propósito: las escalas
/// que ya existen no agrupan, y las construcciones posicionales existentes no se tocan.</param>
public sealed record PriceScaleInput(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    PriceScaleRestriction? Restriction,
    int? Multiple,
    decimal? FinalUsd,
    decimal? FinalCop,
    bool AllowGrouping = false);
