namespace Modules.Quotations.Domain;

/// <summary>
/// Por qué una tasa de la fuente vigente aplica o no (spec retenciones por UVT §5.1). El orden de los miembros no es el
/// de evaluación: ése lo fija <c>WithholdingCandidates.Explain</c> (consumidor final, empresa, moneda, municipio y
/// después el umbral).
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
}

/// <summary>
/// El veredicto de una tasa congelada de la fuente vigente (spec retenciones por UVT §5.1): si aplica, por qué, la base
/// que se comparó (el subtotal de los productos sin IVA, aunque no haya aplicado) y el monto (0 si no aplicó).
/// </summary>
public sealed record WithholdingVerdict(WithholdingRate Rate, WithholdingReason Reason, decimal Base, decimal Amount)
{
    public bool Applies => Reason == WithholdingReason.Applied;

    /// <summary>El umbral en dinero (<see cref="WithholdingRate.Threshold"/>); null sin base mínima, o si la base está en
    /// unidades y no hay valor de la unidad.</summary>
    public decimal? MinimumBaseAmount => Rate.MinimumBase is null ? null : Rate.Threshold;
}
