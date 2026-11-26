namespace Modules.Hr.Domain;

/// <summary>Qué registro de un historial con fecha de vigencia rige hoy.</summary>
public static class EffectiveDated
{
    public static T CurrentOf<T>(IReadOnlyList<T> records, DateOnly today)
        where T : IEffectiveDated
    {
        // Effective dates are strictly increasing within a contract, so there are no ties.
        var inForce = records.Where(record => record.EffectiveDate <= today).MaxBy(record => record.EffectiveDate);
        return inForce ?? records.MinBy(record => record.EffectiveDate)!; // contract not started yet: show its first record
    }

    public static bool IsScheduled(IEffectiveDated record, DateOnly today) => record.EffectiveDate > today;
}
