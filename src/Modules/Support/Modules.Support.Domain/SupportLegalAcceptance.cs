namespace Modules.Support.Domain;

/// <summary>
/// What a requester was shown and accepted when filing (spec 2026-11-15 §5.2, plan L13): one row per ticket and document kind, with a
/// <b>snapshot</b> of the HTML that was served, the question shown, the policy URL, the characterization notice and the visitor's IP.
/// <b>Legal evidence, insert-only</b>: no mutators, no FK (neither to the ticket nor to the version), written in the same transaction
/// as the ticket, and the <c>Down</c> of <c>AddSupportLegalDocuments</c> does not drop the table. What it proves is the text the person
/// saw, not today's.
/// <list type="bullet">
/// <item><see cref="SupportLegalDocumentKind.PrivacyNotice"/>: IP, question and characterization notice are required.</item>
/// <item><see cref="SupportLegalDocumentKind.AnonymousNotice"/>: never an IP («read and continued», not a consent).</item>
/// <item><see cref="SupportLegalDocumentKind.Terms"/>: the IP of a named filing, none on an anonymous one. The anonymity lives in the
/// ticket, so the handler passes the IP only on the named path.</item>
/// </list>
/// </summary>
public sealed class SupportLegalAcceptance
{
    /// <summary>An IPv6 in text fits in 45 characters. One with a scope id (<c>fe80::1%eth0</c>) can be longer: it is cut to this, like
    /// <c>BuyerSession.IpAddress</c> (final review N3), instead of failing the filing.</summary>
    public const int VisitorIpMaxLength = 45;

    public const int CharacterizationTextMaxLength = 1_000;

    // EF Core materializes through here; Record is the only code entry point.
    private SupportLegalAcceptance()
    {
        ContentHtml = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public SupportLegalDocumentKind Kind { get; private set; }

    public Guid DocumentVersionId { get; private set; }

    public int DocumentVersion { get; private set; }

    /// <summary>The time of the filing. On an <see cref="SupportLegalDocumentKind.AnonymousNotice"/> it means «continued after reading
    /// it».</summary>
    public DateTimeOffset AcceptedAt { get; private set; }

    public string ContentHtml { get; private set; }

    public string? AuthorizationQuestion { get; private set; }

    public string? PolicyUrl { get; private set; }

    public string? CharacterizationText { get; private set; }

    public string? VisitorIp { get; private set; }

    /// <param name="servedHtml">Exactly what <c>GET /form</c> served: the version's HTML after sanitizing it again.</param>
    /// <param name="authorizationQuestion">The question as it was shown (the tenant's or the default one). Privacy notice only.</param>
    /// <param name="characterizationText"><see cref="SupportLegalNotices.Characterization"/>. Privacy notice only.</param>
    public static SupportLegalAcceptance Record(
        Guid id,
        Guid tenantId,
        SupportTicketId ticketId,
        SupportLegalDocumentVersion version,
        string servedHtml,
        string? authorizationQuestion,
        string? characterizationText,
        string? visitorIp,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An acceptance needs its id.", nameof(id));
        }

        if (version.IsExample || version.Version < 1 || version.TenantId != tenantId)
        {
            throw new ArgumentException("An acceptance needs a published legal document version of the same tenant.", nameof(version));
        }

        if (string.IsNullOrWhiteSpace(servedHtml) || servedHtml.Length > SupportLegalDocumentVersion.ContentMaxLength)
        {
            throw new ArgumentException(
                $"The served HTML must have between 1 and {SupportLegalDocumentVersion.ContentMaxLength} characters.",
                nameof(servedHtml));
        }

        var isPrivacy = version.Kind == SupportLegalDocumentKind.PrivacyNotice;
        var ip = Optional(NormalizedIp(visitorIp), VisitorIpMaxLength, nameof(visitorIp));
        var question = Optional(authorizationQuestion, SupportLegalDocumentVersion.AuthorizationQuestionMaxLength, nameof(authorizationQuestion));
        var characterization = Optional(characterizationText, CharacterizationTextMaxLength, nameof(characterizationText));

        if (isPrivacy && (ip is null || question is null || characterization is null))
        {
            throw new ArgumentException(
                "A privacy notice acceptance needs the visitor IP, the question shown and the characterization notice.", nameof(visitorIp));
        }

        if (!isPrivacy && (question is not null || characterization is not null))
        {
            throw new ArgumentException(
                "Only a privacy notice acceptance keeps the question and the characterization notice.", nameof(authorizationQuestion));
        }

        if (version.Kind == SupportLegalDocumentKind.AnonymousNotice && ip is not null)
        {
            throw new ArgumentException("An anonymous notice acceptance never keeps an IP.", nameof(visitorIp));
        }

        return new SupportLegalAcceptance
        {
            Id = id,
            TenantId = tenantId,
            TicketId = ticketId,
            Kind = version.Kind,
            DocumentVersionId = version.Id,
            DocumentVersion = version.Version,
            AcceptedAt = now,
            ContentHtml = servedHtml,
            AuthorizationQuestion = question,
            PolicyUrl = version.PolicyUrl,
            CharacterizationText = characterization,
            VisitorIp = ip,
        };
    }

    // Null stays null; a value is trimmed and has to fit. A blank one counts as a value that is not there.
    // Final review N3: trimmed and cut to the column, like BuyerSession.Issue. Blank stays blank, so Optional still rejects it.
    private static string? NormalizedIp(string? visitorIp) =>
        visitorIp?.Trim() is { Length: > VisitorIpMaxLength } trimmed ? trimmed[..VisitorIpMaxLength] : visitorIp;

    private static string? Optional(string? value, int maxLength, string name)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new ArgumentException($"The value must have between 1 and {maxLength} characters.", name);
        }

        return trimmed;
    }
}
