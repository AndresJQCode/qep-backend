using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record GetConversationQuery(Guid TenantId, Guid ConversationId) : IQuery<ConversationSummary>;

/// <summary>§5.3: la misma forma que un ítem de la lista; 404 <c>messaging.conversation.not_found</c>
/// dentro del tenant de la ruta (otro tenant ya respondió 403 al autorizar).</summary>
public sealed class GetConversationHandler(
    IConversationQueries queries,
    ConversationSummaryBuilder summaries,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetConversationQuery, ConversationSummary>
{
    public async Task<ConversationSummary> HandleAsync(GetConversationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var row = await queries.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(query.ConversationId);
        return (await summaries.BuildAsync(query.TenantId, [row], cancellationToken))[0];
    }
}
