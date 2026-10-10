using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

internal sealed class ConversationRepository(MessagingDbContext dbContext) : IConversationRepository
{
    public Task<Conversation?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        dbContext.Conversations.SingleOrDefaultAsync(
            conversation => conversation.TenantId == tenantId && conversation.Id == conversationId, cancellationToken);
}
