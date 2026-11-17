using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record DismissUnclassifiedCommand(Guid TenantId, Guid ConversationId, Guid MessageId) : ICommand<SupportUnclassifiedDto>;

/// <summary>
/// «De ninguno» (spec §8.2, plan E8): la fila queda <c>Dismissed</c> y cada ticket candidato abierto recibe la nota
/// <c>MessageDismissed</c> en su traza; el mensaje no entra a ningún expediente. Fila que ya no está pendiente: 422
/// <c>support.unclassified.not_pending</c>. Sin <c>If-Match</c>: la ruta es de la conversación; si otro cambió un candidato a la vez, el
/// guardado choca por versión y responde 412.
/// </summary>
public sealed class DismissUnclassifiedHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory membershipDirectory,
    ISupportConversationDirectory conversations,
    ISupportTicketRepository tickets,
    ISupportUnclassifiedRepository unclassified,
    ISupportMemberDirectory members,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    SupportTicketDtoComposer composer)
    : ICommandHandler<DismissUnclassifiedCommand, SupportUnclassifiedDto>
{
    private static readonly string[] ChangedFields = ["resolution"];

    public async Task<SupportUnclassifiedDto> HandleAsync(DismissUnclassifiedCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.Manage, cancellationToken);
        var actor = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SupportAuthorization.Denied();
        var conversation = await SupportConversationRules.EnsureConversationAsync(
            conversations, command.TenantId, command.ConversationId, cancellationToken);
        var row = await UnclassifiedRules.EnsurePendingAsync(unclassified, command.TenantId, conversation.Id, command.MessageId, cancellationToken);

        var context = await composer.ReadContextAsync(command.TenantId, cancellationToken);
        var now = context.Now;
        row.Dismiss(actor, now);
        var candidates = (await tickets.ListOpenByConversationAsync(command.TenantId, conversation.Id, cancellationToken))
            .Where(ticket => ticket.CoversInbound(row.OccurredAt))
            .ToList();
        foreach (var ticket in candidates)
        {
            ticket.RecordMessageEvent(SupportTicketEventKind.MessageDismissed, row.MessageId, actor, now);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            SupportAuditActions.UnclassifiedDismissed,
            SupportAuditActions.Resources.UnclassifiedMessage,
            row.MessageId.ToString(),
            "success",
            ChangedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        foreach (var ticket in candidates)
        {
            await realtime.TicketChangedAsync(command.TenantId, ticket.Id.Value, CancellationToken.None);
        }

        return await UnclassifiedReader.ReadAsync(
            conversations, tickets, unclassified, members, command.TenantId, conversation.Id, cancellationToken);
    }
}
