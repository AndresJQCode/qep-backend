namespace Modules.Hr.Domain;

public static class LegalParameters
{
    /// <summary>
    /// El valor de mayor <c>EffectiveFrom</c> que no pasa de <paramref name="date"/>; ninguno ⇒ <c>null</c>. Un año que nadie cargó
    /// todavía conserva el último valor (D-8): la tabla no caduca.
    /// </summary>
    public static decimal? Resolve(IEnumerable<LegalParameterValue> values, LegalParameterKey key, DateOnly date) =>
        values
            .Where(value => value.Key == key && value.EffectiveFrom <= date)
            .OrderByDescending(value => value.EffectiveFrom)
            .Select(value => (decimal?)value.Value)
            .FirstOrDefault();
}
