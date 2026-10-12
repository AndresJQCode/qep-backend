using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class CustomerNoteRepository(CustomersDbContext dbContext) : ICustomerNoteRepository
{
    // Sin tracking: sólo se leen para armar la lista.
    public async Task<IReadOnlyList<CustomerNote>> ListByCustomerAsync(
        Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
        await dbContext.CustomerNotes
            .AsNoTracking()
            .Where(note => note.TenantId == tenantId && note.CustomerId == customerId)
            .OrderByDescending(note => note.CreatedAt).ThenByDescending(note => note.Id)
            .ToListAsync(cancellationToken);

    // Con tracking: los llamadores editan o borran la nota y dependen de la unidad de trabajo.
    public Task<CustomerNote?> FindAsync(
        Guid tenantId, CustomerId customerId, CustomerNoteId noteId, CancellationToken cancellationToken) =>
        dbContext.CustomerNotes.SingleOrDefaultAsync(
            note => note.TenantId == tenantId && note.CustomerId == customerId && note.Id == noteId,
            cancellationToken);

    public void Add(CustomerNote note) => dbContext.CustomerNotes.Add(note);

    public void Remove(CustomerNote note) => dbContext.CustomerNotes.Remove(note);
}
