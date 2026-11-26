using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <param name="Kind">The route segment: <c>privacy-notice</c>, <c>anonymous-notice</c> or <c>terms</c>.</param>
public sealed record GetLegalDocumentHistoryQuery(Guid TenantId, string? Kind) : IQuery<SupportLegalDocumentHistoryDto>;

/// <summary>The published versions of a kind, newest first and without content (spec 2026-11-15 §7); empty if none was published.
/// The example is not a version and does not appear.</summary>
public sealed class GetLegalDocumentHistoryHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportMemberDirectory members,
    ISupportViewer viewer)
    : IQueryHandler<GetLegalDocumentHistoryQuery, SupportLegalDocumentHistoryDto>
{
    public async Task<SupportLegalDocumentHistoryDto> HandleAsync(
        GetLegalDocumentHistoryQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var kind = SupportLegalDocuments.ParseKind(query.Kind);

        var versions = await legalDocuments.ListAsync(query.TenantId, kind, cancellationToken);
        var names = await SupportLegalDocuments.AuthorNamesAsync(query.TenantId, versions, members, cancellationToken);
        var me = await viewer.MemberIdAsync(query.TenantId, cancellationToken);
        return new SupportLegalDocumentHistoryDto(
            kind.ToString(),
            versions
                .Select(version => new SupportLegalDocumentVersionItemDto(
                    version.Version, version.CreatedAt, SupportLegalDocuments.Author(version.CreatedBy, names, me)))
                .ToList());
    }
}
