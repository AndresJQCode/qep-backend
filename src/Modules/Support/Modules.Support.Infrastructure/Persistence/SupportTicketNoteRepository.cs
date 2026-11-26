using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportTicketNoteRepository(SupportDbContext dbContext) : ISupportTicketNoteRepository
{
    public void Add(SupportTicketNote note) => dbContext.TicketNotes.Add(note);

    public Task<SupportTicketNote?> FindAsync(Guid tenantId, Guid noteId, CancellationToken cancellationToken) =>
        dbContext.TicketNotes
            .AsNoTracking()
            .Include(note => note.Files)
            .FirstOrDefaultAsync(note => note.TenantId == tenantId && note.Id == noteId, cancellationToken);

    public async Task<IReadOnlyList<SupportTicketNote>> ListAsync(
        Guid tenantId,
        SupportTicketId ticketId,
        (DateTimeOffset CreatedAt, Guid Id)? before,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var notes = dbContext.TicketNotes
            .AsNoTracking()
            .Where(note => note.TenantId == tenantId && note.TicketId == ticketId);
        // Row comparison (created_at, id) < (cursor), like the ticket's file (SupportTicketMessageRepository.ListVisibleAsync).
        if (before is var (createdAt, id))
        {
            notes = notes.Where(note => EF.Functions.LessThan(
                ValueTuple.Create(note.CreatedAt, note.Id), ValueTuple.Create(createdAt, id)));
        }

        return await notes
            .OrderByDescending(note => note.CreatedAt)
            .ThenByDescending(note => note.Id)
            .Take(take)
            .Include(note => note.Files)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }
}
