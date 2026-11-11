using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-10: la página de mensajes resuelve los teléfonos de todas sus tarjetas en una sola llamada al
/// puerto, y no llama si no hay tarjetas.</summary>
public sealed class ListMessagesSharedContactsTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    // Forma del arreglo contacts del webhook de Meta (messages[].contacts).
    private static string Card(params string[] waIds) =>
        "[{ \"name\": { \"formatted_name\": \"Laura Gómez\" }, \"phones\": ["
        + string.Join(", ", waIds.Select(waId => $"{{ \"phone\": \"+{waId}\", \"wa_id\": \"{waId}\", \"type\": \"CELL\" }}"))
        + "] }]";

    private static MessageRow Row(Guid conversationId, MessageKind kind, string? details, string? text = null) =>
        new(Guid.CreateVersion7(), conversationId, MessageDirection.Inbound, kind, text, null, details, MessageStatus.Delivered, null,
            DateTimeOffset.UnixEpoch, null, null, null, null);

    private static (ListMessagesHandler Handler, FakeCustomerDirectory Customers) Build(Guid conversationId, params MessageRow[] rows)
    {
        var conversations = new InMemoryConversationQueries();
        conversations.Rows.Add(new ConversationRow(
            conversationId, Tenant, Guid.CreateVersion7(), UserId: "CO.1", WaId: "573110000000", Username: null, "Laura",
            CustomerId: null, AssignedMemberId: null, ConversationStatus.Open, 0,
            null, null, null, null, null, null, null,
            DateTimeOffset.UnixEpoch, 1));
        var customers = new FakeCustomerDirectory();
        var handler = new ListMessagesHandler(
            conversations,
            new PageMessageQueries(rows),
            new FakeMemberNames(Guid.CreateVersion7()),
            new FakeResolutionReasonNames(),
            new FakeChatbotNames(),
            customers,
            new FakeTenantModules(),
            new FakeExecutionContext(Tenant, Guid.CreateVersion7(), MessagingPermissions.ConversationRead),
            new ListMessagesValidator());
        return (handler, customers);
    }

    [Fact]
    public async Task APageWithoutCardsNeverCallsThePort()
    {
        var conversationId = Guid.CreateVersion7();
        var (handler, customers) = Build(conversationId, Row(conversationId, MessageKind.Text, null, "hola"));

        _ = await handler.HandleAsync(new ListMessagesQuery(Tenant, conversationId, null, null), TestContext.Current.CancellationToken);

        Assert.Empty(customers.SharedPhoneCalls);
    }

    [Fact]
    public async Task APageWithSeveralCardsResolvesEveryNumberOnceInOneCall()
    {
        var conversationId = Guid.CreateVersion7();
        var (handler, customers) = Build(
            conversationId,
            Row(conversationId, MessageKind.Contacts, Card("573001234567", "14155552671")),
            Row(conversationId, MessageKind.Contacts, Card("573001234567", "34612345678")));

        _ = await handler.HandleAsync(new ListMessagesQuery(Tenant, conversationId, null, null), TestContext.Current.CancellationToken);

        var call = Assert.Single(customers.SharedPhoneCalls);
        Assert.Equal(["14155552671", "34612345678", "573001234567"], call.Order(StringComparer.Ordinal));
    }

    private sealed class PageMessageQueries(IReadOnlyList<MessageRow> rows) : IMessageQueries
    {
        public Task<IReadOnlyDictionary<Guid, ReplyTargetRow>> FindReplyTargetsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ReplyTargetRow>>(new Dictionary<Guid, ReplyTargetRow>());

        public Task<MessageCursor?> FindCursorAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MessageRow>> ListThreadAsync(Guid conversationId, MessageCursor? before, int take, CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }
}
