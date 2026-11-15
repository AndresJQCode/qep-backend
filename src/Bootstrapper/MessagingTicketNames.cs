using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Support.Domain;
using Modules.Support.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>Soporte fase 2: el número visible de los tickets que nombran los eventos del hilo, leído de Soporte por tenant e ids. Un id
/// que no existe (o es de otro tenant) no aparece: el hilo dibuja el evento sin número. Lo amarra <c>MessagingTicketNamesTests</c>, en la
/// integración de Soporte, contra el esquema migrado.</summary>
internal sealed class MessagingTicketNames(SupportDbContext context) : IMessagingTicketNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindNumbersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ticketIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ticketIds);
        if (ticketIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = ticketIds.Distinct().Select(id => new SupportTicketId(id)).ToArray();
        var rows = await context.Tickets.AsNoTracking()
            .Where(ticket => ticket.TenantId == tenantId && ids.Contains(ticket.Id))
            .Select(ticket => new { ticket.Id, ticket.Number })
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.Id.Value, row => SupportTicketNumber.Format(row.Number));
    }
}
