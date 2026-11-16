using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>What the ticket detail shows of an acceptance (plan L19): no content, which opens with the snapshot route.</summary>
public sealed record SupportLegalAcceptanceSummary(
    SupportLegalDocumentKind Kind, int Version, DateTimeOffset AcceptedAt, string? VisitorIp);

/// <summary>
/// The acceptances of the public filings (spec 2026-11-15 §5.2). Legal evidence: only inserted, in the same transaction as the ticket.
/// One per ticket and kind (<c>IX_legal_acceptances_ticket_kind</c>: <see cref="SupportErrorCodes.PublicLegalAcceptanceDuplicate"/>).
/// </summary>
public interface ISupportLegalAcceptanceRepository
{
    void Add(SupportLegalAcceptance acceptance);

    /// <summary>The acceptances of the ticket in the order of <see cref="SupportLegalDocumentKinds.All"/>; empty if it has none.</summary>
    Task<IReadOnlyList<SupportLegalAcceptanceSummary>> ListByTicketAsync(
        Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken);

    Task<SupportLegalAcceptance?> FindAsync(
        Guid tenantId, SupportTicketId ticketId, SupportLegalDocumentKind kind, CancellationToken cancellationToken);
}
