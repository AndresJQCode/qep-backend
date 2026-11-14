using Microsoft.EntityFrameworkCore;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

internal sealed class SupportTicketRepository(SupportDbContext dbContext) : ISupportTicketRepository
{
    /// <summary>Los estados con el reloj corriendo: los únicos que pueden estar vencidos o por vencer.</summary>
    private static readonly SupportTicketStatus[] Running =
        SupportTicketStatuses.Ordered.Where(status => !SupportTicketStatuses.StopsTheClock(status)).ToArray();

    public void Add(SupportTicket ticket) => dbContext.Tickets.Add(ticket);

    public Task<SupportTicket?> FindAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
        dbContext.Tickets.FirstOrDefaultAsync(ticket => ticket.TenantId == tenantId && ticket.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SupportTicketEvent>> ListEventsAsync(
        Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
        await dbContext.TicketEvents
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.TicketId == id)
            .OrderBy(item => item.Ordinal)
            .ToListAsync(cancellationToken);

    public async Task<SupportTicketPage> ListAsync(SupportTicketFilter filter, CancellationToken cancellationToken)
    {
        var tickets = Filtered(filter);
        var total = await tickets.CountAsync(cancellationToken);
        var items = await tickets
            .OrderByDescending(ticket => ticket.ReceivedAt)
            .ThenByDescending(ticket => ticket.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new SupportTicketPage(items, total);
    }

    public async Task<SupportInboxTotals> TotalsAsync(
        Guid tenantId, DateOnly today, DateOnly? dueSoonLimit, CancellationToken cancellationToken)
    {
        var tickets = dbContext.Tickets.AsNoTracking().Where(ticket => ticket.TenantId == tenantId);
        var counted = await tickets
            .GroupBy(ticket => ticket.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var running = tickets.Where(ticket => Running.Contains(ticket.Status) && ticket.DueOn != null);
        var overdue = await running.CountAsync(ticket => ticket.DueOn < today, cancellationToken);
        var dueSoon = dueSoonLimit is { } limit
            ? await running.CountAsync(ticket => ticket.DueOn >= today && ticket.DueOn <= limit, cancellationToken)
            : 0;

        // Los seis estados, también en cero: las pestañas de la bandeja no conocen el enum.
        var byStatus = SupportTicketStatuses.Ordered.ToDictionary(
            status => status,
            status => counted.FirstOrDefault(row => row.Status == status)?.Count ?? 0);
        return new SupportInboxTotals(byStatus, overdue, dueSoon);
    }

    private IQueryable<SupportTicket> Filtered(SupportTicketFilter filter)
    {
        var tickets = dbContext.Tickets.AsNoTracking().Where(ticket => ticket.TenantId == filter.TenantId);

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var wanted = statuses.ToArray();
            tickets = tickets.Where(ticket => wanted.Contains(ticket.Status));
        }

        if (filter.RunningOnly)
        {
            tickets = tickets.Where(ticket => Running.Contains(ticket.Status));
        }

        if (filter.TypeId is { } typeId)
        {
            var id = new SupportTicketTypeId(typeId);
            tickets = tickets.Where(ticket => ticket.TypeId == id);
        }

        if (filter.AssigneeMemberId is { } assignee)
        {
            tickets = tickets.Where(ticket => ticket.AssigneeMemberId == assignee);
        }

        if (filter.UnassignedOnly)
        {
            tickets = tickets.Where(ticket => ticket.AssigneeMemberId == null);
        }

        if (filter.DueBefore is { } before)
        {
            tickets = tickets.Where(ticket => ticket.DueOn != null && ticket.DueOn < before);
        }

        if (filter.DueBetween is { } between)
        {
            var (from, to) = between;
            tickets = tickets.Where(ticket => ticket.DueOn != null && ticket.DueOn >= from && ticket.DueOn <= to);
        }

        return Searched(tickets, filter);
    }

    // El radicado, el texto (asunto y solicitante) y los clientes que resolvió quien llama se unen por O: una sola caja de búsqueda.
    private static IQueryable<SupportTicket> Searched(IQueryable<SupportTicket> tickets, SupportTicketFilter filter)
    {
        var pattern = LikeSearch.ContainsPattern(filter.SearchTerm);
        var customerIds = filter.SearchCustomerIds is { Count: > 0 } ids ? ids.ToArray() : [];
        if (filter.Number is null && pattern is null && customerIds.Length == 0)
        {
            return tickets;
        }

        var number = filter.Number ?? 0;
        var hasNumber = filter.Number is not null;
        var hasPattern = pattern is not null;
        var like = pattern ?? string.Empty;
        return tickets.Where(ticket =>
            (hasNumber && ticket.Number == number) ||
            (ticket.CustomerId != null && customerIds.Contains(ticket.CustomerId.Value)) ||
            (hasPattern && (
                EF.Functions.ILike(ticket.Subject, like, LikeSearch.EscapeCharacter) ||
                EF.Functions.ILike(ticket.Requester!.FirstNames!, like, LikeSearch.EscapeCharacter) ||
                EF.Functions.ILike(ticket.Requester!.LastNames!, like, LikeSearch.EscapeCharacter) ||
                EF.Functions.ILike(ticket.Requester!.BusinessName!, like, LikeSearch.EscapeCharacter) ||
                EF.Functions.ILike(ticket.Requester!.DocumentNumber!, like, LikeSearch.EscapeCharacter))));
    }
}
