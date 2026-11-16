using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// The versions of the legal documents (spec 2026-11-15 §5.1). Legal evidence, insert-only: no method edits or deletes. Two publications
/// of the same version of a kind collide on <c>IX_legal_document_versions_tenant_kind_version</c>:
/// <see cref="SupportErrorCodes.LegalDocumentsVersionTaken"/>. Every read is untracked.
/// </summary>
public interface ISupportLegalDocumentRepository
{
    void Add(SupportLegalDocumentVersion version);

    /// <summary>The highest version of the kind, or null if the tenant never published it (the console shows the example).</summary>
    Task<SupportLegalDocumentVersion?> CurrentAsync(Guid tenantId, SupportLegalDocumentKind kind, CancellationToken cancellationToken);

    /// <summary>The current version of each kind that has one: a kind never published is missing from the dictionary.</summary>
    Task<IReadOnlyDictionary<SupportLegalDocumentKind, SupportLegalDocumentVersion>> CurrentAllAsync(
        Guid tenantId, CancellationToken cancellationToken);

    Task<SupportLegalDocumentVersion?> FindAsync(
        Guid tenantId, SupportLegalDocumentKind kind, int version, CancellationToken cancellationToken);

    /// <summary>Every published version of the kind, newest first.</summary>
    Task<IReadOnlyList<SupportLegalDocumentVersion>> ListAsync(
        Guid tenantId, SupportLegalDocumentKind kind, CancellationToken cancellationToken);
}
