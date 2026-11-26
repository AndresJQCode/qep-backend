namespace Modules.Hr.Domain;

/// <summary>
/// Un tramo de un valor legal desde una fecha: límites en múltiplos de SMMLV y la tasa que se aplica adentro. Dato de la plataforma,
/// sin <c>tenant_id</c>, igual que <see cref="LegalParameterValue"/>. El límite inferior es inclusivo; el superior sólo en el último
/// tramo (lo decide quien lee la lista completa). Sin tope, <see cref="UpperBound"/> es <c>null</c>. Un conjunto de tramos se
/// reemplaza entero (se borran sus filas y se escriben las nuevas), así que no tiene <c>Replace</c>: cada fila nace con su autor.
/// </summary>
public sealed class LegalParameterBracket
{
    // EF Core materializa por acá.
    private LegalParameterBracket()
    {
    }

    public LegalParameterBracket(LegalBracketKey key, DateOnly effectiveFrom, decimal lowerBound, decimal? upperBound, decimal rate)
    {
        Key = key;
        EffectiveFrom = effectiveFrom;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        Rate = rate;
    }

    public LegalBracketKey Key { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public decimal LowerBound { get; private set; }

    public decimal? UpperBound { get; private set; }

    public decimal Rate { get; private set; }

    /// <summary>Cuándo se escribió desde la consola; <c>null</c> en los tramos sembrados por migración.</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>El usuario (<c>IExecutionContext.SubjectId</c>) que lo escribió; <c>null</c> en los tramos sembrados.</summary>
    public Guid? UpdatedBy { get; private set; }

    public static LegalParameterBracket Create(
        LegalBracketKey key, DateOnly effectiveFrom, LegalBracketInput input, Guid by, DateTimeOffset at) =>
        new(key, effectiveFrom, input.LowerBound, input.UpperBound, input.Rate) { UpdatedAt = at, UpdatedBy = by };
}
