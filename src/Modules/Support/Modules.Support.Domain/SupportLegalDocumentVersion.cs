namespace Modules.Support.Domain;

/// <summary>
/// One version of one legal document of a tenant (spec 2026-11-15 §5.1, plan L1 and L5). <b>Legal evidence, insert-only</b>: each
/// publication creates the next version of its kind and none is edited or deleted, so there are no mutators, no FK, and the
/// <c>Down</c> of <c>AddSupportLegalDocuments</c> does not drop the table. The current version is the highest one of
/// <c>(tenant, kind)</c>. The HTML is always the output of <c>ISupportHtmlSanitizer</c> (<see cref="SanitizedHtml"/>).
/// </summary>
public sealed class SupportLegalDocumentVersion
{
    /// <summary>The sanitized HTML (spec §6.4).</summary>
    public const int ContentMaxLength = 60_000;

    /// <summary>The raw body, before sanitizing: a cap against abuse, checked by the validator.</summary>
    public const int RawContentMaxLength = 120_000;

    public const int AuthorizationQuestionMaxLength = 500;

    public const int PolicyUrlMaxLength = 500;

    // Cuánto del token desconocido se muestra en el mensaje del 422.
    private const int UnknownVariableShownLength = 40;

    // EF Core materializes through here; Create and Example are the only code entry points.
    private SupportLegalDocumentVersion()
    {
        ContentHtml = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportLegalDocumentKind Kind { get; private set; }

    /// <summary>Unique per tenant and kind (<c>IX_legal_document_versions_tenant_kind_version</c>). 0 is the example, never stored.</summary>
    public int Version { get; private set; }

    public string ContentHtml { get; private set; }

    /// <summary>Only on <see cref="SupportLegalDocumentKind.PrivacyNotice"/> (D-1). Null means the default question.</summary>
    public string? AuthorizationQuestion { get; private set; }

    /// <summary>Only on <see cref="SupportLegalDocumentKind.PrivacyNotice"/> (D-2): an absolute https URL.</summary>
    public string? PolicyUrl { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The membership that published it. Listed in <c>SupportUserReferenceProbe</c>.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>QEP's sample text (version 0, plan L9): shown to the console until the tenant publishes its own; never stored and never
    /// accepted.</summary>
    public bool IsExample { get; private set; }

    /// <summary>
    /// The validator already gives the field; the rules are repeated here so the domain does not depend on its caller. Without visible
    /// text it is <see cref="SupportErrorCodes.LegalDocumentsContentEmpty"/>; longer than <see cref="ContentMaxLength"/>,
    /// <see cref="SupportErrorCodes.LegalDocumentsContentTooLong"/>. A question or a URL on another kind, a URL that is not https or a
    /// question out of bounds is an <see cref="ArgumentException"/>. Blank optional texts are stored as null. Un <c>{{…}}</c> que no
    /// está en <see cref="SupportLegalVariables.All"/>, en el HTML o en la pregunta, es
    /// <see cref="SupportErrorCodes.LegalDocumentUnknownVariable"/> (spec 2026-11-16).
    /// </summary>
    public static SupportLegalDocumentVersion Create(
        Guid id,
        Guid tenantId,
        SupportLegalDocumentKind kind,
        int version,
        SanitizedHtml content,
        string? authorizationQuestion,
        string? policyUrl,
        Guid createdBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (id == Guid.Empty || createdBy == Guid.Empty)
        {
            throw new ArgumentException("A legal document version needs its id and its author.", nameof(id));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown legal document kind.");
        }

        if (!content.HasVisibleText)
        {
            throw new SupportDomainException(
                SupportErrorCodes.LegalDocumentsContentEmpty, "The legal document has no visible text after sanitizing.");
        }

        if (content.Html.Length > ContentMaxLength)
        {
            throw new SupportDomainException(
                SupportErrorCodes.LegalDocumentsContentTooLong,
                $"The legal document has more than {ContentMaxLength} characters after sanitizing.");
        }

        var question = ValidQuestion(authorizationQuestion);
        var url = ValidPolicyUrl(policyUrl);
        if (kind != SupportLegalDocumentKind.PrivacyNotice && (question is not null || url is not null))
        {
            throw new ArgumentException(
                "Only the privacy notice has an authorization question and a policy URL.", nameof(authorizationQuestion));
        }

        // Spec 2026-11-16 (D-F8): se guarda la plantilla con sus tokens, así que uno mal escrito tiene que frenarse acá; si no, el
        // portal lo mostraría literal. Se mira el HTML ya saneado, que es lo que se guarda y lo que se resuelve al servir.
        if (SupportLegalVariables.UnknownIn(content.Html).Concat(SupportLegalVariables.UnknownIn(question)).FirstOrDefault() is { } unknown)
        {
            // Recortado: lo que hay entre llaves puede ser un párrafo entero, y el mensaje viaja en la respuesta y en los logs.
            var shown = unknown.Length > UnknownVariableShownLength ? unknown[..UnknownVariableShownLength] + "…" : unknown;
            throw new SupportDomainException(
                SupportErrorCodes.LegalDocumentUnknownVariable, $"The legal document uses an unknown variable: '{{{{{shown}}}}}'.");
        }

        return new SupportLegalDocumentVersion
        {
            Id = id,
            TenantId = tenantId,
            Kind = kind,
            Version = version,
            ContentHtml = content.Html,
            AuthorizationQuestion = question,
            PolicyUrl = url,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }

    /// <summary>A sample of <see cref="SupportLegalDocumentExamples"/>: version 0, without author or date.</summary>
    internal static SupportLegalDocumentVersion Example(Guid tenantId, SupportLegalDocumentKind kind, string contentHtml) =>
        new()
        {
            TenantId = tenantId,
            Kind = kind,
            Version = 0,
            IsExample = true,
            ContentHtml = contentHtml,
        };

    /// <summary>An absolute <c>https</c> URL of at most <see cref="PolicyUrlMaxLength"/> characters (moved from the phase 3 legal
    /// texts, plan L5).</summary>
    public static bool IsValidPolicyUrl(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= PolicyUrlMaxLength
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>Plain text of 1 to <see cref="AuthorizationQuestionMaxLength"/> characters once trimmed, without control characters
    /// (line breaks and tabs included): it is shown on one line next to «Sí acepto / No acepto».</summary>
    public static bool IsValidAuthorizationQuestion(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= AuthorizationQuestionMaxLength
            && !trimmed.Any(char.IsControl);
    }

    private static string? ValidQuestion(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return IsValidAuthorizationQuestion(trimmed)
            ? trimmed
            : throw new ArgumentException(
                $"The authorization question must be plain text of at most {AuthorizationQuestionMaxLength} characters.",
                nameof(value));
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
