namespace Modules.Identity.Infrastructure;

// Binding fuertemente tipado de la sección "Authentication:Session" de appsettings.
// Los consumidores inyectan IOptions<QepSessionOptions>; la validación corre al arrancar
// (ver SessionOptionsValidator).
public sealed class QepSessionOptions
{
    public const string SectionName = "Authentication:Session";

    public string CookieName { get; init; } = "qep_session";

    public int AbsoluteLifetimeDays { get; init; } = 30;

    public int IdleTimeoutDays { get; init; } = 7;
}
