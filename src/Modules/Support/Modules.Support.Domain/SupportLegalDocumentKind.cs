namespace Modules.Support.Domain;

/// <summary>
/// The three legal documents of the public portal (spec 2026-11-15 §5.1, plan L1). Stored by name (<c>varchar(32)</c>) and sent by name
/// in bodies and responses; the route uses the kebab-case segment of <see cref="SupportLegalDocumentKinds"/>.
/// </summary>
public enum SupportLegalDocumentKind
{
    PrivacyNotice,
    AnonymousNotice,
    Terms,
}

/// <summary>The only translation between <see cref="SupportLegalDocumentKind"/> and its route segment (plan L1).</summary>
public static class SupportLegalDocumentKinds
{
    /// <summary>In the order the console and the ticket detail list them.</summary>
    public static readonly IReadOnlyList<SupportLegalDocumentKind> All =
        [SupportLegalDocumentKind.PrivacyNotice, SupportLegalDocumentKind.AnonymousNotice, SupportLegalDocumentKind.Terms];

    /// <summary><c>privacy-notice</c>, <c>anonymous-notice</c> or <c>terms</c>, exactly: no other spelling or casing parses.</summary>
    public static bool TryParseRoute(string? segment, out SupportLegalDocumentKind kind)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(segment, ToRoute(candidate), StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static string ToRoute(SupportLegalDocumentKind kind) => kind switch
    {
        SupportLegalDocumentKind.PrivacyNotice => "privacy-notice",
        SupportLegalDocumentKind.AnonymousNotice => "anonymous-notice",
        SupportLegalDocumentKind.Terms => "terms",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown legal document kind."),
    };
}
