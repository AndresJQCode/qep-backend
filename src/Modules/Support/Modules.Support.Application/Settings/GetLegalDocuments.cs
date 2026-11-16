using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

public sealed record GetLegalDocumentsQuery(Guid TenantId) : IQuery<SupportLegalDocumentsDto>;

/// <summary>The current version of each legal document (spec 2026-11-15 §7); a kind never published comes as QEP's example
/// (<see cref="SupportLegalDocumentExamples"/>), never stored. Read by whoever reads the settings (<see cref="SupportPolicies.SettingsView"/>).</summary>
public sealed class GetLegalDocumentsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportMemberDirectory members,
    ISupportViewer viewer,
    ITenantDirectory tenantDirectory,
    ISupportHtmlSanitizer sanitizer)
    : IQueryHandler<GetLegalDocumentsQuery, SupportLegalDocumentsDto>
{
    public async Task<SupportLegalDocumentsDto> HandleAsync(GetLegalDocumentsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewSettingsAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var current = await legalDocuments.CurrentAllAsync(query.TenantId, cancellationToken);
        var documents = SupportLegalDocumentKinds.All
            .Select(kind => current.TryGetValue(kind, out var version) ? version : SupportLegalDocumentExamples.For(kind, query.TenantId))
            .ToList();
        var names = await SupportLegalDocuments.AuthorNamesAsync(query.TenantId, documents, members, cancellationToken);
        var defaultQuestion = await SupportLegalDocuments.DefaultQuestionAsync(query.TenantId, tenantDirectory, cancellationToken);
        var me = await viewer.MemberIdAsync(query.TenantId, cancellationToken);

        return new SupportLegalDocumentsDto(
            SupportLegalDocuments.ToDto(documents[0], names, defaultQuestion, me, sanitizer),
            SupportLegalDocuments.ToDto(documents[1], names, defaultQuestion, me, sanitizer),
            SupportLegalDocuments.ToDto(documents[2], names, defaultQuestion, me, sanitizer));
    }
}

/// <summary>What the console handlers of the legal documents share: the route segment, the default question and the DTO.</summary>
internal static class SupportLegalDocuments
{
    /// <summary>The kind of the <c>{kind}</c> route segment; an unknown one is 404 <see cref="SupportErrorCodes.LegalDocumentsKindNotFound"/>
    /// (plan L1).</summary>
    public static SupportLegalDocumentKind ParseKind(string? segment) =>
        SupportLegalDocumentKinds.TryParseRoute(segment, out var kind)
            ? kind
            : throw new ResourceNotFoundException(
                SupportErrorCodes.LegalDocumentsKindNotFound, $"Legal document kind '{segment}' was not found.");

    /// <summary>The question the portal shows when the tenant wrote none (plan L7), with the tenant's name. A tenant without a row
    /// (only the stub's simulated one) has no name, so there is no default to offer.</summary>
    public static async Task<string?> DefaultQuestionAsync(
        Guid tenantId, ITenantDirectory tenantDirectory, CancellationToken cancellationToken) =>
        await tenantDirectory.GetDisplayNameAsync(new TenantId(tenantId), cancellationToken) is { } name
        && !string.IsNullOrWhiteSpace(name)
            ? SupportLegalNotices.DefaultAuthorizationQuestion(name)
            : null;

    /// <summary>The names of the authors of the published versions, in one call. The example has no author.</summary>
    public static Task<IReadOnlyDictionary<Guid, string>> AuthorNamesAsync(
        Guid tenantId,
        IEnumerable<SupportLegalDocumentVersion> versions,
        ISupportMemberDirectory members,
        CancellationToken cancellationToken) =>
        members.NamesAsync(
            tenantId, versions.Where(version => !version.IsExample).Select(version => version.CreatedBy).Distinct().ToList(), cancellationToken);

    /// <summary>An author no longer found shows as «Sin nombre», as in the trace; <c>IsMe</c> is measured against whoever asks
    /// (phase 4a, plan G8).</summary>
    public static SupportMemberDto Author(Guid memberId, IReadOnlyDictionary<Guid, string> names, Guid? viewerMemberId) =>
        SupportTicketDtoComposer.ToMemberDto(memberId, names, viewerMemberId);

    /// <summary>The content goes through <see cref="ISupportHtmlSanitizer"/> again on every read (final review S5): stored versions are
    /// already its fixed point, so the bytes do not change, but a row edited by hand never reaches the editor unsanitized. The row is
    /// not touched.</summary>
    public static SupportLegalDocumentDto ToDto(
        SupportLegalDocumentVersion version,
        IReadOnlyDictionary<Guid, string> names,
        string? defaultQuestion,
        Guid? viewerMemberId,
        ISupportHtmlSanitizer sanitizer)
    {
        var isPrivacy = version.Kind == SupportLegalDocumentKind.PrivacyNotice;
        return new SupportLegalDocumentDto(
            version.Kind.ToString(),
            version.Version,
            version.IsExample,
            sanitizer.Sanitize(version.ContentHtml).Html,
            version.AuthorizationQuestion,
            version.PolicyUrl,
            isPrivacy ? defaultQuestion : null,
            version.IsExample ? null : version.CreatedAt,
            version.IsExample ? null : Author(version.CreatedBy, names, viewerMemberId));
    }
}
