using System.Text.RegularExpressions;

namespace Modules.Support.Domain;

/// <summary>
/// Una versión de la configuración del formulario público (spec §9.3 «Versionado», plan F14). Insert-only: guardar crea la versión
/// siguiente y la anterior no se toca, así cada ticket apunta a la versión con la que se radicó. No tiene mutadores.
/// </summary>
public sealed partial class SupportFormVersion
{
    public const int IntroTextMaxLength = 1000;

    public const int SuccessMessageMaxLength = 500;

    public const int AccentColorLength = 7;

    // EF Core materializa por acá; Create y Default son los únicos puntos de entrada de código.
    private SupportFormVersion()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Única por tenant (<c>IX_form_versions_tenant_version</c>). 0 es la de <see cref="Default"/>, que no se guarda.</summary>
    public int Version { get; private set; }

    public bool IsPublished { get; private set; }

    /// <summary>El par documento (tipo y número): se muestra y se exige junto (spec §9.3).</summary>
    public bool ShowDocument { get; private set; }

    public bool ShowAddress { get; private set; }

    public bool ShowLandline { get; private set; }

    /// <summary>Con NIT, si la razón social es obligatoria: el único obligatorio que configura el tenant (spec §9.3).</summary>
    public bool BusinessNameRequired { get; private set; }

    /// <summary>Spec §9.3 «Adjuntos: el tenant los permite o no». Apagado, la página esconde el control y el backend responde 422
    /// <c>support.public.attachments_disabled</c> a abrir, completar y radicar con adjuntos. Arranca prendido (owner, 2026-11-15, E2): el tenant lo apaga si no los quiere.</summary>
    public bool AllowAttachments { get; private set; }

    /// <summary><c>#</c> y seis hexadecimales; null usa el color por defecto de la página.</summary>
    public string? AccentColor { get; private set; }

    public string? IntroText { get; private set; }

    public string? SuccessMessage { get; private set; }

    /// <summary>Spec 2026-11-15 §9.3 (plan L29): the tenant company that answers on the portal (name, document type and number). Real FK
    /// to <c>companies.companies</c> with <c>ON DELETE RESTRICT</c>, written by hand in <c>AddSupportFormOrganization</c>: a company
    /// some version chose cannot be deleted. Null = none chosen; required to publish (D-8).</summary>
    public Guid? OrganizationCompanyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>La membresía que guardó la versión. Entra en <c>SupportUserReferenceProbe</c>.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>El validador de la API da el campo; acá se repite la regla para que el dominio no dependa de quien llama. Los textos
    /// se recortan y uno vacío queda en null.</summary>
    public static SupportFormVersion Create(
        Guid id,
        Guid tenantId,
        int version,
        bool isPublished,
        bool showDocument,
        bool showAddress,
        bool showLandline,
        bool businessNameRequired,
        bool allowAttachments,
        string? accentColor,
        string? introText,
        string? successMessage,
        Guid createdBy,
        DateTimeOffset now,
        Guid? organizationCompanyId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (id == Guid.Empty || createdBy == Guid.Empty)
        {
            throw new ArgumentException("A form version needs its id and its author.", nameof(id));
        }

        if (organizationCompanyId == Guid.Empty)
        {
            throw new ArgumentException("The organization company cannot be the empty UUID.", nameof(organizationCompanyId));
        }

        return new SupportFormVersion
        {
            Id = id,
            TenantId = tenantId,
            Version = version,
            IsPublished = isPublished,
            ShowDocument = showDocument,
            ShowAddress = showAddress,
            ShowLandline = showLandline,
            BusinessNameRequired = businessNameRequired,
            AllowAttachments = allowAttachments,
            AccentColor = ValidAccentColor(accentColor),
            IntroText = OptionalText(introText, IntroTextMaxLength, nameof(introText)),
            SuccessMessage = OptionalText(successMessage, SuccessMessageMaxLength, nameof(successMessage)),
            OrganizationCompanyId = organizationCompanyId,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }

    /// <summary>El formulario de un tenant sin versiones (plan F14): sin publicar, todo visible, la razón social obligatoria y con
    /// adjuntos (owner, 2026-11-15, E2). Versión 0, sin id: no se guarda.</summary>
    public static SupportFormVersion Default(Guid tenantId) =>
        new()
        {
            TenantId = tenantId,
            Version = 0,
            IsPublished = false,
            ShowDocument = true,
            ShowAddress = true,
            ShowLandline = true,
            BusinessNameRequired = true,
            AllowAttachments = true,
        };

    public static bool IsValidAccentColor(string? value) => value is not null && AccentColorPattern().IsMatch(value);

    private static string? ValidAccentColor(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return IsValidAccentColor(trimmed)
            ? trimmed
            : throw new ArgumentException("The accent color must be '#' followed by six hexadecimal digits.", nameof(value));
    }

    private static string? OptionalText(string? value, int maxLength, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"The text must have at most {maxLength} characters.", name);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AccentColorPattern();
}
