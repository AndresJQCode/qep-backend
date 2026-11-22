using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Support.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Soporte retiene a un usuario mientras alguna de sus membresías sea la responsable de un ticket (<c>tickets.assignee_member_id</c>)
/// o lo haya radicado (<c>tickets.created_by_member_id</c>, spec 2026-11-22), o haya actuado en su traza (<c>ticket_events.actor_member_id</c>), haya escrito una respuesta (<c>ticket_replies.author_member_id</c>)
/// o haya resuelto un entrante sin clasificar (<c>unclassified_messages.resolved_by_member_id</c>), o haya guardado una versión del
/// formulario público o de los textos legales (<c>form_versions.created_by</c>, <c>legal_text_versions.created_by</c>, fase 3) o de un
/// documento legal (<c>legal_document_versions.created_by</c>, spec 2026-11-15, plan L22), o haya
/// escrito una nota interna (<c>ticket_notes.author_member_id</c>, spec 2026-11-15, plan N23), o haya firmado una respuesta formal
/// como responsable del ticket (<c>ticket_reply_documents.signer_member_id</c>, fase 4b, H3), precedente
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
                await dbContext.Tickets.AnyAsync(ticket => ticket.CreatedByMemberId == membershipId, cancellationToken) ||
                await dbContext.TicketEvents.AnyAsync(item => item.ActorMemberId == membershipId, cancellationToken) ||
                await dbContext.TicketReplies.AnyAsync(reply => reply.AuthorMemberId == membershipId, cancellationToken) ||
                await dbContext.UnclassifiedMessages.AnyAsync(
                    item => item.ResolvedByMemberId == membershipId, cancellationToken) ||
                await dbContext.FormVersions.AnyAsync(form => form.CreatedBy == membershipId, cancellationToken) ||
                await dbContext.LegalTextVersions.AnyAsync(text => text.CreatedBy == membershipId, cancellationToken) ||
                await dbContext.LegalDocumentVersions.AnyAsync(
                    document => document.CreatedBy == membershipId, cancellationToken) ||
                await dbContext.TicketNotes.AnyAsync(note => note.AuthorMemberId == membershipId, cancellationToken) ||
                await dbContext.ReplyDocuments.AnyAsync(
                    document => document.SignerMemberId == membershipId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
