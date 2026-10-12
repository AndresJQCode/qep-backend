using Microsoft.Extensions.Caching.Memory;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Meta;

/// <summary>
/// Spec 2026-10-11 §5.1: el catálogo de plantillas aprobadas de una conexión, normalizado por <see cref="TemplateCatalog"/> y guardado
/// 5 minutos en <c>IMemoryCache</c> por <c>(tenant, conexión)</c> (decisión 6: se crean y aprueban en WhatsApp Manager, acá sólo se
/// leen). <c>refresh</c> se salta la caché y la reemplaza. La conexión se resuelve siempre, también con la caché llena: una conexión
/// pausada, borrada o de otro tenant no entrega plantillas de la caché de otro momento. Una lista que Meta no contesta no se guarda.
/// </summary>
internal sealed class MessageTemplateCatalog(IMessagingConnectionDirectory connections, IWhatsAppCloudClient meta, IMemoryCache cache) : IMessageTemplates
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<MessageTemplateDto>> ListAsync(Guid tenantId, Guid connectionId, bool refresh, CancellationToken cancellationToken)
    {
        var source = await connections.ResolveTemplateSourceAsync(tenantId, connectionId, cancellationToken)
            ?? throw new MessagingDomainException(MessagingErrorCodes.ConnectionUnavailable, "The WhatsApp connection is paused, needs attention or was deleted.");
        var key = (tenantId, connectionId);
        if (!refresh && cache.TryGetValue(key, out IReadOnlyList<MessageTemplateDto>? cached) && cached is not null)
        {
            return cached;
        }

        var result = await meta.ListTemplatesAsync(source, cancellationToken);
        if (!result.Succeeded || result.Value is null)
        {
            throw new MessagingDomainException(MessagingErrorCodes.TemplatesUnavailable, "WhatsApp did not answer the list of templates.");
        }

        var catalog = TemplateCatalog.Normalize(result.Value);
        cache.Set(key, catalog, Lifetime);
        return catalog;
    }
}
