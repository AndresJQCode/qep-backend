namespace Modules.Hr.Domain;

/// <summary>
/// Un tramo de un valor legal desde una fecha: límites en múltiplos de SMMLV y la tasa que se aplica adentro. Dato de la plataforma,
/// sin <c>tenant_id</c>, igual que <see cref="LegalParameterValue"/>. El límite inferior es inclusivo; el superior sólo en el último
/// tramo (lo decide quien lee la lista completa). Sin tope, <see cref="UpperBound"/> es <c>null</c>.
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
}
