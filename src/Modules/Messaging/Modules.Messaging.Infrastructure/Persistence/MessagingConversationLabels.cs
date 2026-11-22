using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-11-22 §8.5: two reads per call, whatever the number of conversations: the conversation rows and the customers they
/// point at. The customer wins over the profile name, as in the inbox.</summary>
internal sealed class MessagingConversationLabels(MessagingDbContext dbContext, IMessagingCustomerDirectory customers) : IMessagingConversationLabels
{
    public async Task<IReadOnlyDictionary<Guid, string>> ContactNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        if (conversationIds.Count == 0)
        {
            return names;
        }

        var ids = conversationIds.ToArray();
        // The tenant filter is the isolation: an id of another tenant is simply not found.
        var rows = await dbContext.Conversations.AsNoTracking()
            .Where(conversation => conversation.TenantId == tenantId && ids.Contains(conversation.Id))
            .Select(conversation => new { conversation.Id, conversation.CustomerId, conversation.ProfileName })
            .ToListAsync(cancellationToken);

        var customerIds = rows.Where(row => row.CustomerId is not null).Select(row => row.CustomerId!.Value).Distinct().ToArray();
        var refs = customerIds.Length == 0
            ? new Dictionary<Guid, CustomerRefDto>()
            : (await customers.FindRefsAsync(tenantId, customerIds, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var row in rows)
        {
            var name = row.CustomerId is { } customerId && refs.TryGetValue(customerId, out var customer) && !string.IsNullOrWhiteSpace(customer.Name)
                ? customer.Name
                : row.ProfileName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[row.Id] = name;
            }
        }

        return names;
    }
}
