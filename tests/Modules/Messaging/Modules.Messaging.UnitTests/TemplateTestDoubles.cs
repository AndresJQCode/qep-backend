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

    public List<Guid> OptedOutByButton { get; } = [];

    public Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        OptedOutByButton.Add(customerId);
        return Task.CompletedTask;
    }

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

    /// <summary>Spec 2026-10-11 §4.4: la audiencia que devuelve <see cref="ListAudienceAsync"/>, ya sin duplicados y en el orden del puerto.</summary>
    public List<MessagingCustomerContact> Audience { get; } = [];

    public async IAsyncEnumerable<MessagingCustomerContact> ListAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var contact in Audience)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return contact;
        }

        await Task.CompletedTask;
    }

    public MessagingAudienceCount NextCount { get; set; } = new(0, 0, 0, 0);

    public List<(IReadOnlyCollection<Guid> TagIds, IReadOnlyCollection<Guid> CustomerIds)> CountCalls { get; } = [];

    public Task<MessagingAudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        CountCalls.Add((tagIds, customerIds));
        return Task.FromResult(NextCount);
    }

    public List<MessagingTagRef> Tags { get; } = [];

    public Task<IReadOnlyList<MessagingTagRef>> FindTagsAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessagingTagRef>>(Tags.Where(tag => tagIds.Contains(tag.Id)).ToList());
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

/// <summary>Spec 2026-10-14 §4.3: el espejo en memoria. Filtra por tenant y conexión como el real; el orden es ordinal.</summary>
internal sealed class FakeMessageTemplateRepository : IMessageTemplateRepository
{
    public List<MessageTemplate> Rows { get; } = [];

    public HashSet<(string Name, string Language)> ActiveCampaignTemplates { get; } = [];

    public Dictionary<(Guid TenantId, Guid ConnectionId), DateTimeOffset> Syncs { get; } = [];

    private IEnumerable<MessageTemplate> Of(Guid tenantId, Guid connectionId) =>
        Rows.Where(row => row.TenantId == tenantId && row.ConnectionId == connectionId);

    public Task<MessageTemplate?> FindAsync(Guid tenantId, Guid connectionId, Guid templateId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).SingleOrDefault(row => row.Id == templateId));

    public Task<MessageTemplate?> FindByMetaIdAsync(Guid tenantId, Guid connectionId, string metaTemplateId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).SingleOrDefault(row => row.MetaTemplateId == metaTemplateId));

    public Task<MessageTemplate?> FindByNameAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).SingleOrDefault(row => row.Name == name && row.Language == language));

    public Task<MessageTemplate?> FindQuotationAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Of(tenantId, connectionId).FirstOrDefault(row => row.Kind == MessageTemplateKind.Quotation));

    public Task<IReadOnlyList<MessageTemplate>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessageTemplate>>(Ordered(Of(tenantId, connectionId)));

    public Task<IReadOnlyList<MessageTemplate>> ListApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MessageTemplate>>(Ordered(Of(tenantId, connectionId).Where(row => row.IsApproved)));

    public void Add(MessageTemplate messageTemplate) => Rows.Add(messageTemplate);

    public void Remove(MessageTemplate messageTemplate) => Rows.Remove(messageTemplate);

    public Task<bool> IsReferencedByActiveCampaignAsync(Guid tenantId, Guid connectionId, string name, string language, CancellationToken cancellationToken) =>
        Task.FromResult(ActiveCampaignTemplates.Contains((name, language)));

    public Task<DateTimeOffset?> LastSyncAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Syncs.TryGetValue((tenantId, connectionId), out var at) ? at : (DateTimeOffset?)null);

    public Task MarkSyncedAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Syncs[(tenantId, connectionId)] = now;
        return Task.CompletedTask;
    }

    private static MessageTemplate[] Ordered(IEnumerable<MessageTemplate> rows) =>
        rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Language, StringComparer.Ordinal).ToArray();
}
