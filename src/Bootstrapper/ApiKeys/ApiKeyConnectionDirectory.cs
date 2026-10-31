using Modules.ApiAccess.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// ApiAccess no ve Messaging: las conexiones <c>whatsapp-cloud</c> del tenant entran por este adaptador.
/// Usa <c>ListAllAsync</c> y filtra WhatsApp en vez de <c>ListNamesAsync</c> para no depender de que
/// aquel también traiga las conexiones de otro canal.
/// </summary>
internal sealed class ApiKeyConnectionDirectory(IMessagingConnectionDirectory connections) : IApiKeyConnectionDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await connections.ListAllAsync(tenantId, cancellationToken))
            .Where(connection => connection.Channel == ConversationChannel.WhatsApp)
            .ToDictionary(connection => connection.Id, connection => connection.Name);
}
