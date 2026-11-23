using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportTicketAccessRepository(SupportDbContext dbContext) : ISupportTicketAccessRepository
{
    public void Add(SupportTicketAccess access) => dbContext.TicketAccess.Add(access);

    public Task<SupportTicketAccess?> FindAsync(Guid tenantId, SupportTicketId ticketId, CancellationToken cancellationToken) =>
        dbContext.TicketAccess.FirstOrDefaultAsync(
            access => access.TenantId == tenantId && access.TicketId == ticketId, cancellationToken);

    public async Task<(SupportTicket Ticket, SupportTicketAccess Access)?> FindByNumberAsync(
        Guid tenantId, long number, CancellationToken cancellationToken)
    {
        // El ticket sin tracking: la consulta no lo modifica, y así un guardado del intento nunca le sube la versión.
        var ticket = await dbContext.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TenantId == tenantId && !item.IsInternal && item.Number == number, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var access = await FindAsync(tenantId, ticket.Id, cancellationToken);
        return access is null ? null : (ticket, access);
    }

    public async Task<(SupportTicket Ticket, SupportTicketAccess Access)?> LockByNumberAsync(
        Guid tenantId, long number, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Locking a ticket access needs an open transaction (ISupportUnitOfWork.BeginTransactionAsync).");
        }

        var ticket = await dbContext.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TenantId == tenantId && !item.IsInternal && item.Number == number, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        // SELECT * para que EF reciba todas las columnas mapeadas, y sin componer nada encima: EF envolvería el FOR UPDATE en una
        // subconsulta (gotcha del FromSql crudo, como QuotationSettingsStore).
        var rows = await dbContext.TicketAccess
            .FromSql($"SELECT * FROM support.ticket_access WHERE ticket_id = {ticket.Id.Value} AND tenant_id = {tenantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? (ticket, rows[0]) : null;
    }
}

internal sealed class SupportFormRepository(SupportDbContext dbContext) : ISupportFormRepository
{
    public void Add(SupportFormVersion form) => dbContext.FormVersions.Add(form);

    public Task<SupportFormVersion?> CurrentAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.FormVersions
            .AsNoTracking()
            .Where(form => form.TenantId == tenantId)
            .OrderByDescending(form => form.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SupportFormVersion?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.FormVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(form => form.TenantId == tenantId && form.Id == id, cancellationToken);
}
