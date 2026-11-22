namespace Modules.Hr.Domain;

public static class LegalParameters
{
    /// <summary>
    /// El valor de mayor <c>EffectiveFrom</c> que no pasa de <paramref name="date"/>; ninguno ⇒ <c>null</c>. Un año que nadie cargó
    /// todavía conserva el último valor (D-8): la tabla no caduca.
    /// </summary>
    public static decimal? Resolve(IEnumerable<LegalParameterValue> values, LegalParameterKey key, DateOnly date) =>
        ResolveEntry(values, key, date)?.Value;

    /// <summary>La fila vigente a <paramref name="date"/> con su <c>EffectiveFrom</c>; ninguna ⇒ <c>null</c>.</summary>
    public static LegalParameterEntry? ResolveEntry(IEnumerable<LegalParameterValue> values, LegalParameterKey key, DateOnly date) =>
        values
            .Where(value => value.Key == key && value.EffectiveFrom <= date)
            .OrderByDescending(value => value.EffectiveFrom)
            .Select(value => new LegalParameterEntry(value.Key, value.EffectiveFrom, value.Value))
            .FirstOrDefault();

    /// <summary>Todos los tramos de la mayor <c>EffectiveFrom</c> que no pasa de <paramref name="date"/>, por límite inferior.</summary>
    public static IReadOnlyList<LegalParameterBracket> ResolveBrackets(
        IEnumerable<LegalParameterBracket> brackets, LegalBracketKey key, DateOnly date)
    {
        var ofKey = brackets.Where(bracket => bracket.Key == key && bracket.EffectiveFrom <= date).ToArray();
        if (ofKey.Length == 0)
        {
            return [];
        }

        var inForce = ofKey.Max(bracket => bracket.EffectiveFrom);
        return ofKey
            .Where(bracket => bracket.EffectiveFrom == inForce)
            .OrderBy(bracket => bracket.LowerBound)
            .ToArray();
    }
}
