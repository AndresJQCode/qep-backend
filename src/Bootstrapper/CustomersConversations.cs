using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-26 D-5 (<c>medium</c>): la conversación por la que un miembro revoca la autorización de datos, por
/// <see cref="IConversationQueries.FindAsync"/> (sin seguimiento). El canal viaja por nombre de <c>ConversationChannel</c> y
/// <c>ChannelUserId</c> es el <c>UserId</c> de la conversación (BSUID o IGSID): Customers no referencia Mensajería.
/// </summary>
internal sealed class CustomersConversations(IConversationQueries queries) : ICustomerConversations
{
    public async Task<CustomerConversationRef?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        await queries.FindAsync(tenantId, conversationId, cancellationToken) is { } row
            ? new CustomerConversationRef(row.Id, row.CustomerId, row.Channel.ToString(), row.UserId, row.WaId)
            : null;
}
