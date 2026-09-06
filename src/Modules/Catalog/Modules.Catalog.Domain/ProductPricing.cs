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
}

/// <summary>
/// Una escala de precio tal como la manda el cliente, sin id: el conjunto completo se
/// reemplaza en cada `PUT`, así que <see cref="Product"/> asigna un <see cref="PriceScaleId"/>
/// nuevo a cada una — el mismo criterio que ya usa <c>ProductDetails</c> para sus cinco
/// opcionales.
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
    int? PackagingUnit,
    decimal? FinalUsd,
    decimal? FinalCop,
    bool AllowGrouping = false);
