using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>§8.6, «Servir»: null = 404 sin código (medio no copiado, mensaje sin medio o inexistente en el tenant).</summary>
public sealed record GetMediaQuery(Guid TenantId, Guid MessageId) : IQuery<MediaStreamDto?>;

public sealed class GetMediaHandler(
    IMediaReads media,
    IMessagingMediaStore store,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetMediaQuery, MediaStreamDto?>
{
    public async Task<MediaStreamDto?> HandleAsync(GetMediaQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        var stored = await media.FindStoredAsync(query.TenantId, query.MessageId, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var opened = await store.OpenReadAsync(stored.StorageKey, cancellationToken);
        return opened is null ? null : opened with { ContentType = stored.MimeType, FileName = stored.FileName };
    }
}
