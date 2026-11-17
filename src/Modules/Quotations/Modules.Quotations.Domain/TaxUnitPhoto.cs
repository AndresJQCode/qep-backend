namespace Modules.Quotations.Domain;

/// <summary>
/// El valor de la unidad tributaria de un país y el año del que salió, como lo resolvería hoy la foto de una tasa
/// (spec retenciones por UVT §5.4). Es el gemelo de <c>TaxUnitResolution</c> de Catalog: Quotations no referencia
/// Catalog, así que el adaptador traduce de uno al otro.
/// </summary>
public readonly record struct TaxUnitPhoto(decimal Value, int Year);
