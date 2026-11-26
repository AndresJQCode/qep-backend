using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="Kind">The route segment: <c>privacy-notice</c>, <c>anonymous-notice</c> or <c>terms</c>.</param>
public sealed record GetLegalDocumentVersionQuery(Guid TenantId, string? Kind, int Version) : IQuery<SupportLegalDocumentDto>;

/// <summary>One published version of a kind, current or old (spec 2026-11-15 §7). One that does not exist, version 0 included (the
/// example is not stored), is 404 <see cref="SupportErrorCodes.LegalDocumentsVersionNotFound"/>.</summary>
public sealed class GetLegalDocumentVersionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportMemberDirectory members,
    ISupportViewer viewer,
    ITenantDirectory tenantDirectory,
    ISupportHtmlSanitizer sanitizer)
    : IQueryHandler<GetLegalDocumentVersionQuery, SupportLegalDocumentDto>
{
    public async Task<SupportLegalDocumentDto> HandleAsync(GetLegalDocumentVersionQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var kind = SupportLegalDocuments.ParseKind(query.Kind);

        var version = await legalDocuments.FindAsync(query.TenantId, kind, query.Version, cancellationToken)
            ?? throw new ResourceNotFoundException(
                SupportErrorCodes.LegalDocumentsVersionNotFound,
                $"Version {query.Version} of legal document '{query.Kind}' was not found.");
        var names = await SupportLegalDocuments.AuthorNamesAsync(query.TenantId, [version], members, cancellationToken);
        var defaultQuestion = kind == SupportLegalDocumentKind.PrivacyNotice
            ? await SupportLegalDocuments.DefaultQuestionAsync(query.TenantId, tenantDirectory, cancellationToken)
            : null;
        return SupportLegalDocuments.ToDto(
            version, names, defaultQuestion, await viewer.MemberIdAsync(query.TenantId, cancellationToken), sanitizer);
    }
}
