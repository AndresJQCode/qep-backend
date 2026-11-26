using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// The audit trail of a block edit lists the fields that really changed, like the other configuration entities do, so "who closed the
/// Thursdays?" can be answered from <c>audit.entries</c>. The names are the ones the creation entry uses; the several fields that make up
/// the opening hours collapse into <c>schedule</c>. Call it <b>before</b> <see cref="AvailabilityBlock.Update"/>.
/// </summary>
internal static class BlockChanges
{
    public static IReadOnlyCollection<string> Between(AvailabilityBlock current, BlockDefinition next)
    {
        var changed = new List<string>();
        Add(changed, "name", current.Name != next.Name);
        Add(changed, "kind", current.Kind != next.Kind);
        Add(
            changed,
            "schedule",
            ScheduleKey(current.Weekly, current.OneOff) != ScheduleKey(next.Weekly, next.OneOff)
                || current.ValidFrom != next.ValidFrom
                || current.ValidTo != next.ValidTo
                || current.ClosesOnHolidays != next.ClosesOnHolidays);
        Add(changed, "capacityPerResource", current.CapacityPerResource != next.CapacityPerResource);
        Add(changed, "sessionMode", current.SessionMode != next.SessionMode);
        Add(changed, "allowClientChoice", current.AllowClientChoice != next.AllowClientChoice);
        Add(changed, "assignmentRule", current.AssignmentRule != next.AssignmentRule);
        // The order of the resources is the "first free" assignment order, so a reorder is a change; the services are a set.
        Add(changed, "resourceIds", !current.ResourceIds.Select(id => id.Value).SequenceEqual(next.ResourceIds.Select(id => id.Value)));
        Add(
            changed,
            "serviceIds",
            !current.ServiceIds.Select(id => id.Value).Order().SequenceEqual(next.ServiceIds.Select(id => id.Value).Order()));
        return changed;
    }

    private static void Add(List<string> changed, string field, bool didChange)
    {
        if (didChange)
        {
            changed.Add(field);
        }
    }

    // The ranges are normalized (sorted, non-overlapping), so equal schedules produce equal keys.
    private static string ScheduleKey(IReadOnlyList<WeekdayHours> weekly, DateHours? oneOff) =>
        string.Join('|', weekly.OrderBy(day => day.Day).Select(day => $"{day.Day}:{string.Join(',', day.Ranges)}"))
        + (oneOff is null ? string.Empty : $"#{oneOff.Date:O}:{string.Join(',', oneOff.Ranges)}");
}
