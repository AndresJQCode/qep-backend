namespace Modules.Scheduling.Domain;

internal static class TimeRanges
{
    /// <summary>Ordena por inicio y rechaza solapamientos: dos franjas del mismo día que se cruzan abrirían dos veces el mismo tiempo.</summary>
    public static IReadOnlyList<TimeRange> Normalize(IEnumerable<TimeRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var sorted = ranges.OrderBy(range => range.From).ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index - 1].Overlaps(sorted[index]))
            {
                throw new ArgumentException("The ranges of a day cannot overlap.", nameof(ranges));
            }
        }

        return sorted;
    }
}
