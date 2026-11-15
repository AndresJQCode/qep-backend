namespace Modules.Support.Domain;

/// <summary>
/// Una versión de los textos legales del formulario público de un tenant (spec §9.5, plan F16): la autorización de tratamiento de
/// datos, la política (texto o URL, al menos una) y, si quiere, los términos. <b>Prueba legal, insert-only</b>: cada edición crea la
/// versión siguiente y ninguna se edita ni se borra (precedente <c>customers.data_consents</c>); por eso no tiene mutadores, y el
/// <c>Down</c> de <c>AddSupportPublicForm</c> no borra su tabla. El consentimiento de cada ticket copia el texto
/// (<see cref="SupportConsent"/>).
/// </summary>
public sealed class SupportLegalTextVersion
{
    public const int TextMaxLength = 20_000;

    public const int PolicyUrlMaxLength = 500;

    // EF Core materializa por acá; Create y Example son los únicos puntos de entrada de código.
    private SupportLegalTextVersion()
    {
        AuthorizationText = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Única por tenant (<c>IX_legal_text_versions_tenant_version</c>). 0 es el ejemplo, que no se guarda.</summary>
    public int Version { get; private set; }

    public string AuthorizationText { get; private set; }

    public string? PolicyText { get; private set; }

    public string? PolicyUrl { get; private set; }

    public string? TermsText { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>La membresía que publicó la versión. Entra en <c>SupportUserReferenceProbe</c>.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>El texto base de QEP (spec §9.5): se muestra marcado como ejemplo hasta que el tenant publique el suyo, y no se guarda
    /// ni se puede aceptar.</summary>
    public bool IsExample { get; private set; }

    /// <summary>Los textos se recortan y uno opcional vacío queda en null. El validador de la API da el campo; acá se repite la regla
    /// para que el dominio no dependa de quien llama. Sin política (ni texto ni URL) es
    /// <see cref="SupportErrorCodes.LegalTextsPolicyRequired"/>.</summary>
    public static SupportLegalTextVersion Create(
        Guid id,
        Guid tenantId,
        int version,
        string authorizationText,
        string? policyText,
        string? policyUrl,
        string? termsText,
        Guid createdBy,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (id == Guid.Empty || createdBy == Guid.Empty)
        {
            throw new ArgumentException("A legal text version needs its id and its author.", nameof(id));
        }

        var authorization = authorizationText?.Trim();
        if (string.IsNullOrEmpty(authorization) || authorization.Length > TextMaxLength)
        {
            throw new ArgumentException(
                $"The authorization text must have between 1 and {TextMaxLength} characters.", nameof(authorizationText));
        }

        var policy = OptionalText(policyText, nameof(policyText));
        var url = ValidPolicyUrl(policyUrl);
        if (policy is null && url is null)
        {
            throw new SupportDomainException(
                SupportErrorCodes.LegalTextsPolicyRequired, "The legal texts need a policy text or a policy URL.");
        }

        return new SupportLegalTextVersion
        {
            Id = id,
            TenantId = tenantId,
            Version = version,
            AuthorizationText = authorization,
            PolicyText = policy,
            PolicyUrl = url,
            TermsText = OptionalText(termsText, nameof(termsText)),
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }

    /// <summary>El texto de ejemplo (versión 0, <see cref="IsExample"/>). Es un punto de partida editable, no asesoría: el tenant es
    /// responsable del contenido y conviene que lo revise su abogado (spec §9.5).</summary>
    public static SupportLegalTextVersion Example(Guid tenantId) =>
        new()
        {
            TenantId = tenantId,
            Version = 0,
            IsExample = true,
            AuthorizationText =
                "Al enviar este formulario autorizas de manera previa, expresa e informada a la empresa para recolectar, almacenar, " +
                "usar y tratar tus datos personales con el fin de radicar, gestionar y responder tu petición, queja, reclamo, " +
                "sugerencia o felicitación, y de contactarte sobre ella. Puedes conocer, actualizar, rectificar y suprimir tus " +
                "datos, y revocar esta autorización, en los términos de la Ley 1581 de 2012 y su política de tratamiento de datos.",
            PolicyText =
                "Tratamos tus datos personales sólo para gestionar tu solicitud y cumplir obligaciones legales. Los conservamos " +
                "mientras dure el trámite y el término que exija la ley. Para ejercer tus derechos como titular, escríbenos por los " +
                "canales de contacto de la empresa.",
        };

    private static string? OptionalText(string? value, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= TextMaxLength
            ? trimmed
            : throw new ArgumentException($"The text must have at most {TextMaxLength} characters.", name);
    }

    /// <summary>Una URL absoluta <c>https</c> de hasta <see cref="PolicyUrlMaxLength"/> caracteres, o null si viene vacía.</summary>
    public static bool IsValidPolicyUrl(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= PolicyUrlMaxLength
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }

    private static string? ValidPolicyUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return IsValidPolicyUrl(trimmed)
            ? trimmed
            : throw new ArgumentException(
                $"The policy URL must be an absolute https URL of at most {PolicyUrlMaxLength} characters.", nameof(value));
    }
}
