using Modules.Customers.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-26 D-5 (<c>medium</c>): la conversación por la que se revoca, leída de Mensajería y traducida a lo que Customers
/// guarda como identidad. El canal viaja por nombre; Customers no conoce <see cref="ConversationChannel"/>.
/// </summary>
public sealed class CustomersConversationsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private sealed class StubConversationQueries(ConversationRow? row) : IConversationQueries
    {
        public Task<ConversationRow?> FindAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
            Task.FromResult(row is not null && row.TenantId == tenantId && row.Id == conversationId ? row : null);

        public Task<IReadOnlyList<ConversationRow>> ListAsync(
            Guid tenantId, ConversationListFilter filter, ConversationCursor? after, int take, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConversationCountsDto> CountsAsync(Guid tenantId, Guid? memberId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationRow>> FindManyAsync(
            Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static ConversationRow InstagramRow(Guid customerId) =>
        new(
            Guid.CreateVersion7(), Tenant, Guid.CreateVersion7(), UserId: "17841400000000001", WaId: null, Username: "laura.ruiz",
            ProfileName: "Laura", CustomerId: customerId, AssignedMemberId: null, Status: ConversationStatus.Open, UnreadCount: 0,
            LastInboundAt: null, LastMessageId: null, LastMessageDirection: null, LastMessageKind: null, LastMessagePreview: null,
            LastMessageStatus: null, LastMessageAt: null, UpdatedAt: DateTimeOffset.UnixEpoch, Version: 1,
            Channel: ConversationChannel.Instagram);

    [Fact]
    public async Task TheConversationRowMapsToTheCustomersRef()
    {
        var customerId = Guid.CreateVersion7();
        var row = InstagramRow(customerId);
        var conversations = new CustomersConversations(new StubConversationQueries(row));

        var found = await conversations.FindAsync(Tenant, row.Id, TestContext.Current.CancellationToken);
        var missing = await conversations.FindAsync(Tenant, Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.Equal(new CustomerConversationRef(row.Id, customerId, "Instagram", "17841400000000001", null), found);
        Assert.Null(missing);
    }
}
