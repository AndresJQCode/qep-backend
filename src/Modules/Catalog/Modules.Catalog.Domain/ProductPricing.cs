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

    public decimal? FinalUsd { get; init; }

    public decimal? FinalCop { get; init; }

    /// <summary>Porcentaje, 0 a 100. Null se trata como 0 al validar el precio final.</summary>
    public decimal? Discount { get; init; }

    public IReadOnlyCollection<PriceScaleInput> Scales { get; init; } = [];
}

/// <summary>
/// Una escala de precio tal como la manda el cliente, sin id: el conjunto completo se
/// reemplaza en cada `PUT`, así que <see cref="Product"/> asigna un <see cref="PriceScaleId"/>
/// nuevo a cada una — el mismo criterio que ya usa <c>ProductDetails</c> para sus cinco
/// opcionales.
/// </summary>
public sealed record PriceScaleInput(
    Guid PriceListId,
    int FromUnit,
    int ToUnit,
    decimal Discount,
    PriceScaleRestriction? Restriction,
    int? Multiple,
    int? PackagingUnit,
    decimal? FinalUsd,
    decimal? FinalCop);
