using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper;

/// <summary>§6.5: el wa_id de Meta son los dígitos del E.164; se traduce en las dos direcciones acá.</summary>
internal sealed partial class MessagingCustomerDirectory(
    ICustomerPhoneDirectory phones,
    ICustomerWhatsAppDirectory whatsApp,
    ICustomerInstagramDirectory instagram,
    ICustomerTagLookup tagLookup,
    ICustomerMergeStore mergeStore,
    ILogger<MessagingCustomerDirectory> logger) : IMessagingCustomerDirectory
{
    /// <summary>El tope de <c>FindPhonesByNameAsync</c> (spec §6.5); el puerto de Customers no tiene default.</summary>
    public const int NameCap = 200;

    /// <summary>El tope del filtro por etiqueta de la bandeja: 5000 ids en <c>ANY(@ids)</c> siguen siendo una sola consulta barata. Una
    /// etiqueta con más clientes recorta el filtro, el mismo compromiso que la búsqueda por nombre.</summary>
    public const int TagCap = 5000;

    public async Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken)
    {
        var matches = await phones.MatchAsync(tenantId, waIds.Select(waId => "+" + waId).ToArray(), cancellationToken);
        return matches.ToDictionary(pair => pair.Key.TrimStart('+'), pair => new CustomerRefDto(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<CustomerRefDto>> ListByPhoneAsync(Guid tenantId, string waId, int take, CancellationToken cancellationToken) =>
        [.. (await phones.ListByPhoneAsync(tenantId, "+" + waId, take, cancellationToken)).Select(match => new CustomerRefDto(match.Id, match.Name, match.IsComplete))];

    // Spec 2026-10-24 §3.3: el mismo patrón que PipelineCustomerDirectory.
    public async Task<Guid> ResolveSurvivorAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await mergeStore.FindSurvivorIdAsync(tenantId, customerId, cancellationToken) ?? customerId;

    public async Task<IReadOnlyDictionary<Guid, CustomerRefDto>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? new Dictionary<Guid, CustomerRefDto>()
            : (await whatsApp.FindRefsAsync(tenantId, ids, cancellationToken)).ToDictionary(pair => pair.Key, pair => new CustomerRefDto(pair.Value.Id, pair.Value.Name, pair.Value.IsComplete));

    public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        whatsApp.FindIdsByNameAsync(tenantId, term, NameCap, cancellationToken);

    public async Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken) =>
        (await phones.FindPhonesByNameAsync(tenantId, term, NameCap, cancellationToken)).Select(phone => phone.TrimStart('+')).ToArray();

    public async Task<MessagingEnsuredCustomer> EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (contact.Channel == Modules.Messaging.Domain.ConversationChannel.Instagram)
        {
            // Spec 2026-10-14 decisión 10: por IGSID, sin teléfono.
            var fromInstagram = await instagram.EnsureAsync(tenantId, new InstagramContact(contact.UserId, contact.Username, contact.ProfileName), cancellationToken);
            return new MessagingEnsuredCustomer(fromInstagram.CustomerId, fromInstagram.Outcome == EnsureOutcome.Created);
        }

        var ensured = await whatsApp.EnsureAsync(
            tenantId,
            new WhatsAppContact(contact.UserId, contact.WaId is null ? null : "+" + contact.WaId, contact.ProfileName, contact.Username),
            cancellationToken);
        return new MessagingEnsuredCustomer(ensured.CustomerId, ensured.Outcome == EnsureOutcome.Created);
    }

    private static MessagingCustomerContact ToMessaging(CustomerContact contact) =>
        new(contact.CustomerId, contact.Name, contact.PhoneE164, contact.WhatsAppUserId, contact.IsActive, contact.MarketingOptOut, contact.CreatedAt);

    public async Task<MessagingCustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        await whatsApp.FindContactAsync(tenantId, customerId, cancellationToken) is { } contact ? ToMessaging(contact) : null;

    public async IAsyncEnumerable<MessagingCustomerContact> ListAudienceAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var contact in whatsApp.ListAudienceAsync(tenantId, tagIds, customerIds, cancellationToken))
        {
            yield return ToMessaging(contact);
        }
    }

    public async Task<MessagingAudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var count = await whatsApp.CountAudienceAsync(tenantId, tagIds, customerIds, cancellationToken);
        return new MessagingAudienceCount(count.Total, count.Inactive, count.OptedOut, count.WithoutWhatsApp);
    }

    public async Task<IReadOnlyList<MessagingTagRef>> FindTagsAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken) =>
        [.. (await tagLookup.FindAsync(tenantId, tagIds, cancellationToken)).Select(tag => new MessagingTagRef(tag.Id, tag.Name, tag.Color))];

    public Task<IReadOnlyList<Guid>> FindIdsByTagAsync(Guid tenantId, Guid tagId, CancellationToken cancellationToken) =>
        tagLookup.FindCustomerIdsAsync(tenantId, tagId, TagCap, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MessagingTagRef>>> FindTagsByCustomerAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) =>
        (await tagLookup.FindForCustomersAsync(tenantId, customerIds, cancellationToken)).ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MessagingTagRef>)[.. pair.Value.Select(tag => new MessagingTagRef(tag.Id, tag.Name, tag.Color))]);

    public Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        whatsApp.MarkMarketingOptOutAsync(tenantId, customerId, cancellationToken);

    public Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        whatsApp.MarkMarketingOptOutByButtonAsync(tenantId, customerId, cancellationToken);

    // §11: sin el BSUID en el log; el tenant basta para buscar la conversación.
    [LoggerMessage(Level = LogLevel.Information, Message = "A WhatsApp number change for tenant {TenantId} was not applied to Customers: no customer had the previous id, or the new one already belongs to another customer.")]
    private static partial void LogNotReplaced(ILogger logger, Guid tenantId);

    public async Task ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken)
    {
        if (!await whatsApp.ReplaceWhatsAppUserIdAsync(tenantId, previous, current, cancellationToken))
        {
            LogNotReplaced(logger, tenantId);
        }
    }
}
