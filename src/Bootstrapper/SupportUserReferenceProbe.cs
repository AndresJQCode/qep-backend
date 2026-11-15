using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Support.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte retiene a un usuario mientras alguna de sus membresías sea la responsable de un ticket (<c>tickets.assignee_member_id</c>)
/// o haya actuado en su traza (<c>ticket_events.actor_member_id</c>), haya escrito una respuesta (<c>ticket_replies.author_member_id</c>)
/// o haya resuelto un entrante sin clasificar (<c>unclassified_messages.resolved_by_member_id</c>), precedente
/// <see cref="LoansUserReferenceProbe"/>. Vive en el
/// composition root porque necesita <see cref="IMembershipDirectory"/> (Tenancy) y el <see cref="SupportDbContext"/> a la vez.
/// </summary>
/// <remarks>
/// Una columna nueva con un id de membresía en el esquema <c>support</c> tiene que sumarse acá, o el usuario que la referencia se
/// borra igual. Los ids que guarda <c>ticket_events.details</c> (responsable anterior y nuevo) no se revisan, a propósito: la
/// traza los muestra como miembro removido (pre-flight R8).
/// </remarks>
internal sealed class SupportUserReferenceProbe(
    SupportDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "support";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        // Una consulta por membresía, como las demás sondas: un usuario tiene una por tenant.
        foreach (var membershipId in membershipIds)
        {
            if (await dbContext.Tickets.AnyAsync(ticket => ticket.AssigneeMemberId == membershipId, cancellationToken) ||
                await dbContext.TicketEvents.AnyAsync(item => item.ActorMemberId == membershipId, cancellationToken) ||
                await dbContext.TicketReplies.AnyAsync(reply => reply.AuthorMemberId == membershipId, cancellationToken) ||
                await dbContext.UnclassifiedMessages.AnyAsync(
                    item => item.ResolvedByMemberId == membershipId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
