using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.1: el catálogo ya normalizado, sin Meta ni caché.</summary>
internal sealed class FakeMessageTemplates : IMessageTemplates
{
    public List<MessageTemplateDto> Catalog { get; } = [];

    public Exception? Failure { get; set; }

    public List<(Guid TenantId, Guid ConnectionId, bool Refresh)> Calls { get; } = [];

    public Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, connectionId, refresh));
        return Failure is { } failure ? throw failure : Task.FromResult<IReadOnlyList<MessageTemplateDto>>(Catalog.ToList());
    }

    /// <summary>Una plantilla enviable con dos variables posicionales.</summary>
    public static MessageTemplateDto Hello(string name = "hola", string language = "es", string category = "UTILITY") =>
        new(name, language, category, TemplateCatalog.Positional, null, "Hola {{1}}, tu pedido {{2}} salió", "Gracias", [], ["1", "2"], true, null);
}

/// <summary>Los clientes que Messaging ve por el puerto (spec 2026-10-11 §4.4). Sólo lo que usan las pruebas de E2.</summary>
internal sealed class FakeCustomerDirectory : IMessagingCustomerDirectory
{
    public Dictionary<Guid, MessagingCustomerContact> Contacts { get; } = [];

    public List<Guid> OptedOut { get; } = [];

    public MessagingCustomerContact Add(string name = "Laura", string? phone = "+573001234567", string? userId = null, bool active = true, bool optOut = false)
    {
        var contact = new MessagingCustomerContact(Guid.CreateVersion7(), name, phone, userId, active, optOut);
        Contacts[contact.CustomerId] = contact;
        return contact;
    }

    public Task<MessagingCustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Contacts.GetValueOrDefault(customerId));

    public Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        OptedOut.Add(customerId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<Guid, CustomerRefDto>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CustomerRefDto>>(
            Contacts.Values.Where(contact => ids.Contains(contact.CustomerId)).ToDictionary(contact => contact.CustomerId, contact => new CustomerRefDto(contact.CustomerId, contact.Name, true)));

    public Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, CustomerRefDto>>(new Dictionary<string, CustomerRefDto>());

    public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MessagingEnsuredCustomer> EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<MessagingCustomerContact> ListAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MessagingAudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed record OpenCall(Guid ConnectionId, MessagingCustomerContact Contact, Guid? AssignedMemberId, Guid? ActorUserId);

/// <summary>Como el real: si el cliente ya tiene conversación en la conexión la devuelve; si no, agrega una fila a la lista del banco.</summary>
internal sealed class FakeConversationOpener(MessagingTestBed bed) : IConversationOpener
{
    public List<OpenCall> Calls { get; } = [];

    public Task<OpenedConversation> OpenAsync(
        Guid tenantId, Guid connectionId, MessagingCustomerContact contact, Guid? assignedMemberId, Guid? actorUserId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Calls.Add(new OpenCall(connectionId, contact, assignedMemberId, actorUserId));
        var existing = bed.Conversations.Rows.FirstOrDefault(row => row.ConnectionId == connectionId && row.CustomerId == contact.CustomerId);
        if (existing is not null)
        {
            return Task.FromResult(new OpenedConversation(existing.Id, false));
        }

        var row = new ConversationRow(
            Guid.CreateVersion7(), tenantId, connectionId, contact.WhatsAppUserId,
            contact.PhoneE164 is null ? null : new string(contact.PhoneE164.Where(char.IsAsciiDigit).ToArray()),
            null, contact.Name, contact.CustomerId, assignedMemberId, ConversationStatus.Open, 0, null, null, null, null, null, null, null, now, 1);
        bed.Conversations.Rows.Add(row);
        return Task.FromResult(new OpenedConversation(row.Id, true));
    }
}
