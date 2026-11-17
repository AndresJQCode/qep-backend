using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Plan F11, extraído en la fase 4b (H17): quién firma un mensaje que el sistema manda por Mensajería en nombre del ticket (la información
/// con el PIN, la encuesta). La fila del mensaje exige un autor: quien creó el ticket (el actor de su evento <c>Created</c>), si sigue
/// activo; si no, el responsable; si tampoco, el primer gestor en el orden de <c>GET /assignees</c> (nombre invariante y el id de
/// desempate). Null sin ninguno: quien llama no manda nada (<c>no_author</c>).
/// </summary>
public sealed class SupportSystemAuthor(ISupportMemberDirectory members, ISupportTicketRepository tickets)
{
    public async Task<Guid?> AuthorAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var created = ticket.Events.FirstOrDefault(item => item.Kind == SupportTicketEventKind.Created)
            ?? (await tickets.ListEventsAsync(ticket.TenantId, ticket.Id, cancellationToken))
                .FirstOrDefault(item => item.Kind == SupportTicketEventKind.Created);
        var candidates = new[] { created?.ActorMemberId, ticket.AssigneeMemberId }.OfType<Guid>().Distinct().ToList();
        if (candidates.Count > 0)
        {
            var found = await members.FindAsync(ticket.TenantId, candidates, cancellationToken);
            foreach (var candidate in candidates)
            {
                if (found.Any(member => member.MemberId == candidate && member.IsActive))
                {
                    return candidate;
                }
            }
        }

        var managers = await members.ListManagersAsync(ticket.TenantId, cancellationToken);
        return managers
            .OrderBy(member => member.DisplayName, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(member => member.MemberId)
            .Select(member => (Guid?)member.MemberId)
            .FirstOrDefault();
    }
}
