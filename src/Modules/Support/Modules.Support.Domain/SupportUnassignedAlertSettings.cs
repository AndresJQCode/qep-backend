namespace Modules.Support.Domain;

/// <summary>
/// Spec 2026-11-22 §7: the tenant's «ticket sin asignar» alert. <paramref name="ThresholdHours"/> are business hours, counted on the tenant's
/// schedule: <paramref name="ScheduleId"/> if it still exists, else the tenant's default, else around the clock (a schedule is data of
/// another module and has no foreign key here, so a deleted one is read as «missing», not as an error). The recipients are members chosen
/// among those who can manage tickets; the handler checks that, and the domain only keeps the shape of the list.
/// </summary>
public sealed record SupportUnassignedAlertSettings(bool Enabled, int ThresholdHours, Guid? ScheduleId, IReadOnlyList<Guid> RecipientIds)
{
    public const int DefaultThresholdHours = 4;

    public const int MinThresholdHours = 1;

    public const int MaxThresholdHours = 72;

    public const int MaxRecipients = 20;

    /// <summary>Off, four hours, the tenant's default schedule and nobody chosen: what a tenant that never saved has.</summary>
    public static SupportUnassignedAlertSettings Default { get; } = new(false, DefaultThresholdHours, null, []);

    /// <summary>The same shape the handler's validator rejects with a field error; the domain does not trust it.</summary>
    internal void EnsureValid()
    {
        if (ThresholdHours is < MinThresholdHours or > MaxThresholdHours)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ThresholdHours),
                ThresholdHours,
                $"The unassigned-alert threshold is between {MinThresholdHours} and {MaxThresholdHours} business hours.");
        }

        ArgumentNullException.ThrowIfNull(RecipientIds);
        if (RecipientIds.Count > MaxRecipients)
        {
            throw new ArgumentException($"The unassigned alert has at most {MaxRecipients} recipients.", nameof(RecipientIds));
        }

        if (RecipientIds.Distinct().Count() != RecipientIds.Count || RecipientIds.Contains(Guid.Empty))
        {
            throw new ArgumentException("The recipients of the unassigned alert are distinct members.", nameof(RecipientIds));
        }

        if (Enabled && RecipientIds.Count == 0)
        {
            throw new ArgumentException("An enabled unassigned alert needs at least one recipient.", nameof(RecipientIds));
        }
    }

    /// <summary>Same values, in the same order of recipients (order is what the screen repaints).</summary>
    public bool SameAs(SupportUnassignedAlertSettings other) =>
        other is not null
        && Enabled == other.Enabled
        && ThresholdHours == other.ThresholdHours
        && ScheduleId == other.ScheduleId
        && RecipientIds.SequenceEqual(other.RecipientIds);
}
