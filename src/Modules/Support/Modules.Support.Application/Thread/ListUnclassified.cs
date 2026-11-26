using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record ListUnclassifiedQuery(Guid TenantId, Guid ConversationId) : IQuery<SupportUnclassifiedDto>;

/// <summary>«¿De qué ticket es?» (spec §8.2, plan E5): los entrantes pendientes de la conversación, por hora del mensaje, cada uno con
/// los tickets abiertos que lo cubren. La pantalla no calcula los candidatos.</summary>
public sealed class ListUnclassifiedHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportConversationDirectory conversations,
    ISupportTicketRepository tickets,
    ISupportUnclassifiedRepository unclassified,
    ISupportMemberDirectory members)
    : IQueryHandler<ListUnclassifiedQuery, SupportUnclassifiedDto>
{
    public async Task<SupportUnclassifiedDto> HandleAsync(ListUnclassifiedQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var conversation = await SupportConversationRules.EnsureConversationAsync(
            conversations, query.TenantId, query.ConversationId, cancellationToken);
        return await UnclassifiedReader.ReadAsync(
            conversations, tickets, unclassified, members, query.TenantId, conversation.Id, cancellationToken);
    }
}

/// <summary>La lista de «sin clasificar» que devuelven leer, asignar y descartar: así la pantalla repinta con la respuesta.</summary>
internal static class UnclassifiedReader
{
    public static async Task<SupportUnclassifiedDto> ReadAsync(
        ISupportConversationDirectory conversations,
        ISupportTicketRepository tickets,
        ISupportUnclassifiedRepository unclassified,
        ISupportMemberDirectory members,
        Guid tenantId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var pending = await unclassified.ListPendingAsync(tenantId, conversationId, cancellationToken);
        if (pending.Count == 0)
        {
            return new SupportUnclassifiedDto([]);
        }

        var open = await tickets.ListOpenByConversationAsync(tenantId, conversationId, cancellationToken);
        var messages = await SupportThreadRules.ReadMessagesAsync(
            conversations, members, tenantId, conversationId, pending.Select(row => row.MessageId).ToList(), cancellationToken);

        return new SupportUnclassifiedDto(pending
            .Where(row => messages.ContainsKey(row.MessageId))
            .Select(row => new SupportUnclassifiedMessageDto(
                messages[row.MessageId],
                open.Where(ticket => ticket.CoversInbound(row.OccurredAt)).Select(SupportThreadRules.ToRef).ToList()))
            .ToList());
    }
}
