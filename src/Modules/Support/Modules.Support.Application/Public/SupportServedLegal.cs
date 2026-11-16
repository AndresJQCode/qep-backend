using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>One current legal document as the portal gets it: the stored version and its HTML sanitized again on the way out.</summary>
internal sealed record SupportServedDocument(SupportLegalDocumentVersion Version, string Html);

/// <summary>
/// The three current legal documents as the public portal is served them (spec 2026-11-15 §6.3 and §9, plan L7, L8, L14 and L17). The
/// one place that turns stored versions into what the visitor sees: <c>GET /form</c> sends it and the filing stores it in the snapshot
/// of each acceptance, so what is recorded is byte for byte what was shown. <b>Every HTML goes through
/// <see cref="ISupportHtmlSanitizer"/> again</b>, whatever is in the column. A missing kind, or one left without visible text once
/// sanitized (only a row written by hand can be), is the same <c>404 support.public.not_found</c> as an unpublished form.
/// </summary>
internal sealed record SupportServedLegal(
    SupportServedDocument PrivacyNotice,
    string AuthorizationQuestion,
    SupportServedDocument AnonymousNotice,
    SupportServedDocument Terms)
{
    public static async Task<SupportServedLegal> ReadAsync(
        ISupportLegalDocumentRepository documents,
        ISupportHtmlSanitizer sanitizer,
        Guid tenantId,
        string tenantName,
        CancellationToken cancellationToken)
    {
        var current = await documents.CurrentAllAsync(tenantId, cancellationToken);

        SupportServedDocument Serve(SupportLegalDocumentKind kind)
        {
            if (!current.TryGetValue(kind, out var version))
            {
                throw SupportPublicResolver.NotFound();
            }

            var served = sanitizer.Sanitize(version.ContentHtml);
            return served.HasVisibleText ? new SupportServedDocument(version, served.Html) : throw SupportPublicResolver.NotFound();
        }

        var privacy = Serve(SupportLegalDocumentKind.PrivacyNotice);
        return new SupportServedLegal(
            privacy,
            privacy.Version.AuthorizationQuestion ?? SupportLegalNotices.DefaultAuthorizationQuestion(tenantName),
            Serve(SupportLegalDocumentKind.AnonymousNotice),
            Serve(SupportLegalDocumentKind.Terms));
    }

    public SupportPublicLegalDto ToDto() =>
        new(
            new SupportPublicPrivacyNoticeDto(
                PrivacyNotice.Version.Version, PrivacyNotice.Html, AuthorizationQuestion, PrivacyNotice.Version.PolicyUrl),
            new SupportPublicLegalDocumentDto(AnonymousNotice.Version.Version, AnonymousNotice.Html),
            new SupportPublicLegalDocumentDto(Terms.Version.Version, Terms.Html),
            SupportLegalNotices.Characterization);
}
