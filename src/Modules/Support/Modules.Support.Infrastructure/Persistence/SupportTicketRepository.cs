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

    // El índice IX_tickets_ticket_type_id de la FK cubre las dos lecturas.
    public Task<bool> AnyOfTypeAsync(Guid tenantId, SupportTicketTypeId typeId, CancellationToken cancellationToken) =>
        dbContext.Tickets.AnyAsync(ticket => ticket.TenantId == tenantId && ticket.TypeId == typeId, cancellationToken);

    public async Task<IReadOnlySet<SupportTicketTypeId>> UsedTypeIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await dbContext.Tickets
            .Where(ticket => ticket.TenantId == tenantId)
            .Select(ticket => ticket.TypeId)
            .Distinct()
            .ToListAsync(cancellationToken))
        .ToHashSet();

    public Task<SupportTicket?> FindAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
        dbContext.Tickets.FirstOrDefaultAsync(ticket => ticket.TenantId == tenantId && ticket.Id == id, cancellationToken);

    public Task<SupportTicket?> FindByClientRequestAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken) =>
        dbContext.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                ticket => ticket.TenantId == tenantId && ticket.ClientRequestId == clientRequestId, cancellationToken);

    public Task<SupportTicketSurvey?> FindLatestSurveyAsync(Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
        dbContext.TicketSurveys
            .AsNoTracking()
            .Where(survey => survey.TenantId == tenantId && survey.TicketId == id)
            .OrderByDescending(survey => survey.ResolutionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SupportTicketSurvey?> FindSurveyByTokenHashAsync(Guid tenantId, byte[] tokenHash, CancellationToken cancellationToken) =>
        dbContext.TicketSurveys
            .AsNoTracking()
            .FirstOrDefaultAsync(survey => survey.TenantId == tenantId && survey.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<SupportTicketSurvey>> ListSurveysAsync(
        Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
        await dbContext.TicketSurveys
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.TicketId == id)
            .OrderBy(item => item.ResolutionNumber)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SupportTicketEvent>> ListEventsAsync(
        Guid tenantId, SupportTicketId id, CancellationToken cancellationToken) =>
        await dbContext.TicketEvents
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.TicketId == id)
            .OrderBy(item => item.Ordinal)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SupportTicket>> ListOpenByConversationAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await dbContext.Tickets
            .Where(ticket => ticket.TenantId == tenantId
                && !ticket.IsInternal
                && ticket.ConversationId == conversationId
                && ticket.Status != SupportTicketStatus.Closed)
            .OrderBy(ticket => ticket.ReceivedAt)
            .ThenBy(ticket => ticket.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SupportTicket>> ListByConversationOrCustomerAsync(
        Guid tenantId, Guid conversationId, Guid? customerId, CancellationToken cancellationToken)
    {
        var hasCustomer = customerId is not null;
        var customer = customerId ?? Guid.Empty;
        return await dbContext.Tickets
            .AsNoTracking()
            .Where(ticket => ticket.TenantId == tenantId
                && !ticket.IsInternal
                && (ticket.ConversationId == conversationId || (hasCustomer && ticket.CustomerId == customer)))
            .OrderByDescending(ticket => ticket.ReceivedAt)
            .ThenByDescending(ticket => ticket.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<SupportTicketPage> ListAsync(SupportTicketFilter filter, CancellationToken cancellationToken)
    {
        var tickets = Filtered(filter);
        var total = await tickets.CountAsync(cancellationToken);
        // La prioridad se guarda por nombre: el rango va en un CASE, así el orden y la página salen de la base. Sin índice para él: la
        // consulta ya va acotada al tenant y un índice sobre la columna no sirve para ordenar por la expresión.
        var ordered = filter.ByPriority
            ? tickets
                .OrderBy(ticket => ticket.Priority == SupportTicketPriority.Urgent ? 0
                    : ticket.Priority == SupportTicketPriority.High ? 1
                    : ticket.Priority == SupportTicketPriority.Medium ? 2
                    : 3)
                .ThenByDescending(ticket => ticket.ReceivedAt)
            : tickets.OrderByDescending(ticket => ticket.ReceivedAt);
        var items = await ordered
            .ThenByDescending(ticket => ticket.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new SupportTicketPage(items, total);
    }

    public async Task<SupportInboxTotals> TotalsAsync(
        Guid tenantId,
        SupportTicketKind kind,
        Guid? participantMemberId,
        DateOnly today,
        DateOnly? dueSoonLimit,
        CancellationToken cancellationToken)
    {
        var tickets = Scoped(
            dbContext.Tickets.AsNoTracking().Where(ticket => ticket.TenantId == tenantId), kind, participantMemberId);
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

    // El alcance de quien lista: el tipo de ticket y, para quien sólo usa tareas, su participación (creador o responsable). Va en la
    // consulta: nunca se traen filas ajenas para descartarlas después.
    private static IQueryable<SupportTicket> Scoped(
        IQueryable<SupportTicket> tickets, SupportTicketKind kind, Guid? participantMemberId)
    {
        tickets = kind switch
        {
            SupportTicketKind.External => tickets.Where(ticket => !ticket.IsInternal),
            SupportTicketKind.Internal => tickets.Where(ticket => ticket.IsInternal),
            _ => tickets,
        };

        return participantMemberId is { } member
            ? tickets.Where(ticket => ticket.CreatedByMemberId == member || ticket.AssigneeMemberId == member)
            : tickets;
    }

    private IQueryable<SupportTicket> Filtered(SupportTicketFilter filter)
    {
        var tickets = Scoped(
            dbContext.Tickets.AsNoTracking().Where(ticket => ticket.TenantId == filter.TenantId),
            filter.Kind,
            filter.ParticipantMemberId);

        if (filter.CreatedByMemberId is { } createdBy)
        {
            tickets = tickets.Where(ticket => ticket.CreatedByMemberId == createdBy);
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var wanted = statuses.ToArray();
            tickets = tickets.Where(ticket => wanted.Contains(ticket.Status));
        }

        if (filter.Priorities is { Count: > 0 } priorities)
        {
            var wanted = priorities.ToArray();
            tickets = tickets.Where(ticket => wanted.Contains(ticket.Priority));
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

    // El radicado, el texto (asunto y nombres del solicitante, no su documento: plan, Tarea 9) y los clientes que resolvió quien
    // llama se unen por O: una sola caja de búsqueda.
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
                EF.Functions.ILike(ticket.Requester!.BusinessName!, like, LikeSearch.EscapeCharacter))));
    }
}
