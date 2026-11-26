namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// The <c>Hr</c> section of appsettings (spec 2026-11-23 §9.1): the daily reminder run of the medical exams. Validated at start, like
/// <c>ServiceContractsOptions</c>.
/// </summary>
public sealed class HrOptions
{
    public const string SectionName = "Hr";

    /// <summary>How often the worker wakes up. It runs more than once a day on purpose: the work of the day is deduplicated by the
    /// run table, and a frequent tick starts soon after <see cref="SendHourLocal"/> in every time zone.</summary>
    public int SweepMinutes { get; init; } = 15;

    /// <summary>The local hour of the tenant from which the digest goes out: nobody gets a reminder at midnight.</summary>
    public int SendHourLocal { get; init; } = 8;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (SweepMinutes <= 0)
        {
            failures.Add("Hr:SweepMinutes must be greater than zero.");
        }

        if (SendHourLocal is < 0 or > 23)
        {
            failures.Add("Hr:SendHourLocal must be an hour between 0 and 23.");
        }

        return failures;
    }
}
