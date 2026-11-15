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
            .FirstOrDefaultAsync(item => item.TenantId == tenantId && item.Number == number, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var access = await FindAsync(tenantId, ticket.Id, cancellationToken);
        return access is null ? null : (ticket, access);
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

internal sealed class SupportLegalTextRepository(SupportDbContext dbContext) : ISupportLegalTextRepository
{
    public void Add(SupportLegalTextVersion text) => dbContext.LegalTextVersions.Add(text);

    public Task<SupportLegalTextVersion?> CurrentAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.LegalTextVersions
            .AsNoTracking()
            .Where(text => text.TenantId == tenantId)
            .OrderByDescending(text => text.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SupportLegalTextVersion?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.LegalTextVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(text => text.TenantId == tenantId && text.Id == id, cancellationToken);
}

internal sealed class SupportConsentRepository(SupportDbContext dbContext) : ISupportConsentRepository
{
    public void Add(SupportConsent consent) => dbContext.Consents.Add(consent);
}
