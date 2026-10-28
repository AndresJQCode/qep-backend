using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>§8.6, «Servir»: null = 404 sin código (medio no copiado, mensaje sin medio o inexistente en el tenant).</summary>
/// <param name="Range">Spec 2026-10-20 D10: el header <c>Range</c> tal cual llegó, o <c>null</c>.</param>
public sealed record GetMediaQuery(Guid TenantId, Guid MessageId, string? Range = null) : IQuery<MediaStreamDto?>;

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

        if (MediaByteRange.Parse(query.Range) is not { } requested)
        {
            var opened = await store.OpenReadAsync(stored.StorageKey, cancellationToken);
            return opened is null ? null : opened with { ContentType = stored.MimeType, FileName = stored.FileName };
        }

        // D10: el tamaño sale del objeto, no de la fila: es lo que de verdad hay para servir. Un 416 no abre nada.
        if (await store.SizeOfAsync(stored.StorageKey, cancellationToken) is not { } total)
        {
            return null;
        }

        var range = MediaByteRange.Resolve(requested, total);
        if (!range.Satisfiable)
        {
            return new MediaStreamDto(Stream.Null, stored.MimeType, 0, stored.FileName, range);
        }

        var slice = await store.OpenReadRangeAsync(stored.StorageKey, range.From, range.To, cancellationToken);
        return slice is null ? null : slice with { ContentType = stored.MimeType, FileName = stored.FileName, Length = range.Length, Range = range };
    }
}
