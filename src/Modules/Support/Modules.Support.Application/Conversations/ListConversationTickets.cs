using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

public sealed record ListConversationTicketsQuery(Guid TenantId, Guid ConversationId) : IQuery<SupportConversationTicketsDto>;

/// <summary>
/// La pestaña de Soporte de la ficha de una conversación (spec §8.1): sus tickets y los del mismo cliente por otros canales, en cualquier
/// estado, del más nuevo al más viejo. Dice si se puede abrir un ticket desde acá y, si no, por qué (<c>messaging_disabled</c> antes que
/// <c>no_permission</c>): la pantalla no conoce módulos ni permisos.
/// </summary>
public sealed class ListConversationTicketsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportConversationDirectory conversations,
    ISupportProvisioner provisioner,
    ISupportTicketRepository tickets,
    ISupportUnclassifiedRepository unclassified,
    SupportTicketDtoComposer composer)
    : IQueryHandler<ListConversationTicketsQuery, SupportConversationTicketsDto>
{
    public const string MessagingDisabled = SupportChannelReasons.MessagingDisabled;

    public const string NoPermission = "no_permission";

    public async Task<SupportConversationTicketsDto> HandleAsync(
        ListConversationTicketsQuery query, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var conversation = await SupportConversationRules.EnsureConversationAsync(
            conversations, query.TenantId, query.ConversationId, cancellationToken);

        // El contexto de plazos exige la fila de ajustes: una ficha puede ser lo primero que un tenant abre de Soporte.
        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        var found = await tickets.ListByConversationOrCustomerAsync(
            query.TenantId, conversation.Id, conversation.CustomerId, cancellationToken);
        var context = await composer.ReadContextAsync(query.TenantId, cancellationToken);
        var rows = await composer.ComposeListAsync(query.TenantId, found, context, cancellationToken);
        var items = found
            .Zip(rows, (ticket, row) => new SupportConversationTicketDto(
                row, ticket.ConversationId == conversation.Id, ticket.FollowThread))
            .ToList();

        var cannotCreateReason =
            !await SupportConversationRules.IsEnabledAsync(tenantModules, query.TenantId, TenantModuleKeys.Messaging, cancellationToken)
                ? MessagingDisabled
                : !executionContext.HasPermission(SupportPermissions.Manage)
                    ? NoPermission
                    : null;

        return new SupportConversationTicketsDto(
            conversation.Id,
            cannotCreateReason is null,
            cannotCreateReason,
            items,
            await unclassified.CountPendingAsync(query.TenantId, conversation.Id, cancellationToken));
    }
}
