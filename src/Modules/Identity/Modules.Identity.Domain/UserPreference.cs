using System.Text.RegularExpressions;

namespace Modules.Identity.Domain;

/// <summary>
/// Cómo ve el producto una persona dentro de un tenant concreto. Su identidad es el par
/// <c>(UserId, TenantId)</c>, no el usuario solo: <c>SDD-OD-17</c> resolvió que la preferencia
/// es del usuario <b>en cada tenant</b>, así que alguien que pertenece a dos organizaciones
/// tiene dos preferencias independientes y cambiar una no toca la otra.
///
/// <para><c>TenantId</c> es un <see cref="Guid"/> desnudo y no una referencia a Tenancy: los
/// módulos no comparten esquema ni se referencian entre sí, y <c>ArchitectureTests</c> lo
/// verifica. La integridad no se pierde porque el tenant que llega ya pasó por la verificación
/// de membresía de <c>ExternalClaimsTransformation</c>.</para>
/// </summary>
public sealed class UserPreference
{
    /// <summary>
    /// La paleta botánica de <c>AUTH-10</c> en modo claro. Elegido por el owner el 2026-08-18:
    /// es lo que el producto ya muestra, así que quien nunca entra a su perfil no ve ningún
    /// cambio y este slice no puede causar una regresión visual.
    /// </summary>
    public const string DefaultColorScheme = "botanical";

    // El backend valida FORMA, no pertenencia a un catálogo. El catálogo de esquemas es del
    // módulo `account` en el frontend (su ficha lo declara): duplicarlo acá crearía dos
    // autoridades sobre lo mismo, y agregar un esquema pasaría a necesitar un deploy del
    // backend. Un identificador desconocido degrada al default en el cliente.
    private static readonly Regex ColorSchemePattern = new(
        "^[a-z0-9-]{1,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private UserPreference()
    {
    }

    private UserPreference(
        UserId userId,
        Guid tenantId,
        string colorScheme,
        ThemeMode mode,
        DateTimeOffset updatedAt)
    {
        UserId = userId;
        TenantId = tenantId;
        ColorScheme = colorScheme;
        Mode = mode;
        UpdatedAt = updatedAt;
    }

    public UserId UserId { get; private set; }

    public Guid TenantId { get; private set; }

    public string ColorScheme { get; private set; } = DefaultColorScheme;

    public ThemeMode Mode { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// La preferencia que ve quien nunca eligió. No se persiste al leerla: una lectura no
    /// escribe, y no tener preferencia es un estado normal, no una fila faltante.
    /// </summary>
    public static UserPreference CreateDefault(
        UserId userId,
        Guid tenantId,
        DateTimeOffset updatedAt) =>
        new(userId, tenantId, DefaultColorScheme, ThemeMode.Light, updatedAt);

    public static UserPreference Create(
        UserId userId,
        Guid tenantId,
        string colorScheme,
        string mode,
        DateTimeOffset updatedAt) =>
        new(
            userId,
            tenantId,
            NormalizeColorScheme(colorScheme),
            ParseMode(mode),
            updatedAt);

    /// <summary>
    /// Reemplaza los dos ejes de una vez. Valida antes de tocar nada: una preferencia
    /// rechazada no puede quedar a medias, con el esquema nuevo y el modo viejo.
    /// </summary>
    public void Change(string colorScheme, string mode, DateTimeOffset updatedAt)
    {
        var normalizedScheme = NormalizeColorScheme(colorScheme);
        var parsedMode = ParseMode(mode);

        ColorScheme = normalizedScheme;
        Mode = parsedMode;
        UpdatedAt = updatedAt;
    }

    public static string NormalizeColorScheme(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!ColorSchemePattern.IsMatch(normalized))
        {
            throw new IdentityDomainException(
                "identity.preference.scheme.invalid",
                "Color scheme must be 1 to 32 characters of lowercase letters, digits or hyphens.");
        }

        return normalized;
    }

    public static ThemeMode ParseMode(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => throw new IdentityDomainException(
                "identity.preference.mode.invalid",
                "Mode must be either 'light' or 'dark'."),
        };
    }

    public static string ToWireValue(ThemeMode mode) =>
        mode == ThemeMode.Dark ? "dark" : "light";
}
