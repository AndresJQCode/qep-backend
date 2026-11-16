using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>The internal notes of a ticket (spec 2026-11-15 §5.4). Insert-only: there is no update nor delete.</summary>
public interface ISupportTicketNoteRepository
{
    void Add(SupportTicketNote note);

    /// <summary>Without tracking, with its files. Null if the tenant has no note with that id, on any ticket.</summary>
    Task<SupportTicketNote?> FindAsync(Guid tenantId, Guid noteId, CancellationToken cancellationToken);

    /// <summary>Without tracking, newest first (<c>created_at DESC, id DESC</c>), strictly before <paramref name="before"/> when
    /// given. Callers ask for one more than the page to know whether another follows.</summary>
    Task<IReadOnlyList<SupportTicketNote>> ListAsync(
        Guid tenantId,
        SupportTicketId ticketId,
        (DateTimeOffset CreatedAt, Guid Id)? before,
        int take,
        CancellationToken cancellationToken);
}
