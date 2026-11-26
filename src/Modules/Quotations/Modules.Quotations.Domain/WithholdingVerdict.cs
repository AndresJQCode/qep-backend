namespace Modules.Quotations.Domain;

/// <summary>
/// Por qué una tasa de la fuente vigente aplica o no (spec retenciones por UVT §5.1). El orden de los miembros no es el
/// de evaluación: ése lo fija <c>WithholdingCandidates.Explain</c> (consumidor final, empresa, moneda, municipio, después
/// las líneas del alcance y al final el umbral).
/// </summary>
public enum WithholdingReason
{
    /// <summary>Entró al cálculo y el subtotal alcanza el umbral.</summary>
    Applied,

    /// <summary>Entró al cálculo y el subtotal no alcanza el umbral.</summary>
    BelowMinimumBase,

    /// <summary>Base mínima en unidades tributarias y ningún valor cargado: no aplica nunca.</summary>
    NoTaxUnitValue,

    /// <summary>El documento factura a consumidor final, que no retiene.</summary>
    FinalConsumer,

    /// <summary>Sin empresa facturadora, o su país no está en la tabla de países.</summary>
    NoBillingCompany,

    /// <summary>La moneda del documento no es la que discrimina impuestos en el país de la empresa.</summary>
    CurrencyWithoutTax,

    /// <summary>Una tasa municipal de un municipio distinto al de la operación.</summary>
    OtherMunicipality,

    /// <summary>
    /// Una tasa de bienes o de servicios en un documento sin líneas de ese tipo, o cuyas líneas suman 0 (spec 2026-11-18
    /// §4.2): la pantalla dice «sin servicios en el documento» y no «bajo la base mínima». Una tasa All nunca lo da: con
    /// base 0 sigue el umbral, como antes de que existiera el alcance. Va al final del enum a
    /// propósito: se serializa por nombre, pero el orden de los de antes no se toca.
    /// </summary>
    NoLinesOfScope,
}

/// <summary>
/// El veredicto de una tasa congelada de la fuente vigente (spec retenciones por UVT §5.1): si aplica, por qué, la base
/// que se comparó (el subtotal sin IVA de las líneas de su alcance, aunque no haya aplicado; spec 2026-11-18 §4.2) y el
/// monto (0 si no aplicó).
/// </summary>
public sealed record WithholdingVerdict(WithholdingRate Rate, WithholdingReason Reason, decimal Base, decimal Amount)
{
    public bool Applies => Reason == WithholdingReason.Applied;

    /// <summary>El umbral en dinero (<see cref="WithholdingRate.Threshold"/>); null sin base mínima, o si la base está en
    /// unidades y no hay valor de la unidad.</summary>
    public decimal? MinimumBaseAmount => Rate.MinimumBase is null ? null : Rate.Threshold;
}
