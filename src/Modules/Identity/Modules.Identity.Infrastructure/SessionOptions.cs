namespace Modules.Identity.Infrastructure;

// Strongly-typed binding of the "Authentication:Session" appsettings section.
// Consumers inject IOptions<QepSessionOptions>; validation runs at startup
// (see SessionOptionsValidator).
public sealed class QepSessionOptions
{
    public const string SectionName = "Authentication:Session";

    public string CookieName { get; init; } = "qep_session";

    public int AbsoluteLifetimeDays { get; init; } = 30;

    public int IdleTimeoutDays { get; init; } = 7;
}
