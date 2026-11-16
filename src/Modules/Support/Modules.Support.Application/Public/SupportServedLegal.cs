using FluentValidation;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>One current legal document as the portal gets it: the stored version and its HTML sanitized again on the way out, con sus variables resueltas.</summary>
internal sealed record SupportServedDocument(SupportLegalDocumentVersion Version, string Html);

/// <summary>
/// The three current legal documents as the public portal is served them (spec 2026-11-15 §6.3 and §9, plan L7, L8, L14 and L17). The
/// one place that turns stored versions into what the visitor sees: <c>GET /form</c> sends it and the filing stores it in the snapshot
/// of each acceptance, so what is recorded is byte for byte what was shown. <b>Every HTML goes through
/// <see cref="ISupportHtmlSanitizer"/> again</b>, whatever is in the column. A missing kind, or one left without visible text once
/// sanitized, or one the sanitizer rejects as too large, too deep or unstable (only a row written by hand can be either), is the same
/// <c>404 support.public.not_found</c> as an unpublished form, never a <c>422</c> the visitor cannot act on (final review N2).
/// <para>Acá, y sólo acá, se resuelven las variables (spec 2026-11-16, D-F8): <c>{{empresa}}</c> y las de la empresa con
/// la empresa que responde, <c>{{nombre_comercial}}</c> con el nombre del tenant. El HTML las lleva codificadas; la pregunta,
/// texto plano, tal cual (V-2). Como la foto de la aceptación guarda lo que sale de acá, guarda lo resuelto: lo que la persona aceptó.
/// Un token de empresa sin empresa, o un texto resuelto que ya no cabe en la foto, es el mismo 404 que cualquier configuración
/// incompleta.</para>
/// </summary>
internal sealed record SupportServedLegal(
    SupportServedDocument PrivacyNotice,
    string AuthorizationQuestion,
    SupportServedDocument AnonymousNotice,
    SupportServedDocument Terms)
{
    /// <param name="organization">La empresa que responde de la versión publicada del formulario
    /// (<see cref="SupportPublicOrganization.OfAsync"/>); null si no tiene.</param>
    public static async Task<SupportServedLegal> ReadAsync(
        ISupportLegalDocumentRepository documents,
        ISupportHtmlSanitizer sanitizer,
        Guid tenantId,
        string tenantName,
        SupportPublicOrganizationDto? organization,
        CancellationToken cancellationToken)
    {
        var current = await documents.CurrentAllAsync(tenantId, cancellationToken);
        var values = new SupportLegalVariableValues(
            tenantName, organization?.Name, organization?.DocumentTypeCode, organization?.DocumentNumber);

        SupportServedDocument Serve(SupportLegalDocumentKind kind)
        {
            if (!current.TryGetValue(kind, out var version))
            {
                throw SupportPublicResolver.NotFound();
            }

            // Not cached per version id on purpose (Block B review): the re-sanitizing exists for a row changed by hand, and a cache
            // keyed by id would keep serving what it saw before that change. The sanitizer behind the port caches by the SHA-256 of the
            // stored content instead (final review S1, CachingSupportHtmlSanitizer), so a hand-edited row is sanitized again.
            SanitizedHtml served;
            try
            {
                served = sanitizer.Sanitize(version.ContentHtml);
            }
            catch (ValidationException)
            {
                throw SupportPublicResolver.NotFound();
            }

            if (!served.HasVisibleText)
            {
                throw SupportPublicResolver.NotFound();
            }

            // Después de sanear: los valores van codificados, así que lo resuelto no necesita otra pasada. El tope es el de la foto
            // (SupportLegalAcceptance.Record): mejor no servir que dejar radicar algo que no se puede guardar como prueba.
            var filled = SupportLegalVariables.FillHtml(served.Html, values);
            return filled is not null && filled.Length <= SupportLegalDocumentVersion.ContentMaxLength
                ? new SupportServedDocument(version, filled)
                : throw SupportPublicResolver.NotFound();
        }

        var privacy = Serve(SupportLegalDocumentKind.PrivacyNotice);
        return new SupportServedLegal(
            privacy,
            Question(privacy.Version.AuthorizationQuestion, tenantName, values),
            Serve(SupportLegalDocumentKind.AnonymousNotice),
            Serve(SupportLegalDocumentKind.Terms));
    }

    // La del tenant, resuelta como texto plano (V-2: el portal la pinta como texto de React, que ya escapa); la de QEP no tiene variables.
    private static string Question(string? stored, string tenantName, SupportLegalVariableValues values)
    {
        if (stored is null)
        {
            return SupportLegalNotices.DefaultAuthorizationQuestion(tenantName);
        }

        var filled = SupportLegalVariables.FillText(stored, values);
        return filled is not null && filled.Trim().Length <= SupportLegalDocumentVersion.AuthorizationQuestionMaxLength
            ? filled
            : throw SupportPublicResolver.NotFound();
    }

    public SupportPublicLegalDto ToDto() =>
        new(
            new SupportPublicPrivacyNoticeDto(
                PrivacyNotice.Version.Version, PrivacyNotice.Html, AuthorizationQuestion, PrivacyNotice.Version.PolicyUrl),
            new SupportPublicLegalDocumentDto(AnonymousNotice.Version.Version, AnonymousNotice.Html),
            new SupportPublicLegalDocumentDto(Terms.Version.Version, Terms.Html),
            SupportLegalNotices.Characterization);
}
