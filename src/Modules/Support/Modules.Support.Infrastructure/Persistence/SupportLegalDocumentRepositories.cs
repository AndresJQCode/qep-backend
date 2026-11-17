using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

// Spec 2026-11-15 §5.1: insert-only, every read untracked.
internal sealed class SupportLegalDocumentRepository(SupportDbContext dbContext) : ISupportLegalDocumentRepository
{
    public void Add(SupportLegalDocumentVersion version) => dbContext.LegalDocumentVersions.Add(version);

    public Task<SupportLegalDocumentVersion?> CurrentAsync(
        Guid tenantId, SupportLegalDocumentKind kind, CancellationToken cancellationToken) =>
        dbContext.LegalDocumentVersions
            .AsNoTracking()
            .Where(document => document.TenantId == tenantId && document.Kind == kind)
            .OrderByDescending(document => document.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<SupportLegalDocumentKind, SupportLegalDocumentVersion>> CurrentAllAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        // One round trip per kind on IX_legal_document_versions_tenant_kind_version: three index seeks, and no history read.
        var current = new Dictionary<SupportLegalDocumentKind, SupportLegalDocumentVersion>();
        foreach (var kind in SupportLegalDocumentKinds.All)
        {
            if (await CurrentAsync(tenantId, kind, cancellationToken) is { } version)
            {
                current[kind] = version;
            }
        }

        return current;
    }

    public Task<SupportLegalDocumentVersion?> FindAsync(
        Guid tenantId, SupportLegalDocumentKind kind, int version, CancellationToken cancellationToken) =>
        dbContext.LegalDocumentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                document => document.TenantId == tenantId && document.Kind == kind && document.Version == version,
                cancellationToken);

    public async Task<IReadOnlyList<SupportLegalDocumentVersion>> ListAsync(
        Guid tenantId, SupportLegalDocumentKind kind, CancellationToken cancellationToken) =>
        await dbContext.LegalDocumentVersions
            .AsNoTracking()
            .Where(document => document.TenantId == tenantId && document.Kind == kind)
            .OrderByDescending(document => document.Version)
            .ToListAsync(cancellationToken);
}

// Spec 2026-11-15 §5.2: only inserted, with the ticket. The detail reads a projection without the snapshot (plan L19).
internal sealed class SupportLegalAcceptanceRepository(SupportDbContext dbContext) : ISupportLegalAcceptanceRepository
{
    public void Add(SupportLegalAcceptance acceptance) => dbContext.LegalAcceptances.Add(acceptance);

    public async Task<IReadOnlyList<SupportLegalAcceptanceSummary>> ListByTicketAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.LegalAcceptances
            .AsNoTracking()
            .Where(acceptance => acceptance.TenantId == tenantId && acceptance.TicketId == ticketId)
            .Select(acceptance => new SupportLegalAcceptanceSummary(
                acceptance.Kind, acceptance.DocumentVersion, acceptance.AcceptedAt, acceptance.VisitorIp))
            .ToListAsync(cancellationToken);

        // The kind is stored by name, so ORDER BY in SQL would be alphabetical: the enum order is applied here.
        return [.. rows.OrderBy(row => row.Kind)];
    }

    public Task<SupportLegalAcceptance?> FindAsync(
        Guid tenantId, SupportTicketId ticketId, SupportLegalDocumentKind kind, CancellationToken cancellationToken) =>
        dbContext.LegalAcceptances
            .AsNoTracking()
            .FirstOrDefaultAsync(
                acceptance => acceptance.TenantId == tenantId && acceptance.TicketId == ticketId && acceptance.Kind == kind,
                cancellationToken);
}
