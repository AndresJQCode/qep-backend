namespace Modules.Audit.Infrastructure;

// Strongly-typed binding of the "Audit" appsettings section. Retention windows follow
// the approved capability model (7 years for security/administration, 2 years for
// operational). The windows are modeled here; automated purge is a follow-up.
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    // ~7 years.
    public int SecurityRetentionDays { get; init; } = 2555;

    // 2 years.
    public int OperationalRetentionDays { get; init; } = 730;
}
