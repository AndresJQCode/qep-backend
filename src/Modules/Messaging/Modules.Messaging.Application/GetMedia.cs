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
        return stored is null ? null : await OpenAsync(store, stored.StorageKey, stored.MimeType, stored.FileName, query.Range, cancellationToken);
    }

    /// <summary>El objeto entero o el rango pedido, con el tipo y el nombre de la fila. Una sola copia para el medio de un mensaje y el de un
    /// programado (spec 2026-11-09: «mismo manejo de rangos»).</summary>
    internal static async Task<MediaStreamDto?> OpenAsync(
        IMessagingMediaStore store, string storageKey, string mimeType, string? fileName, string? rangeHeader, CancellationToken cancellationToken)
    {
        if (MediaByteRange.Parse(rangeHeader) is not { } requested)
        {
            var opened = await store.OpenReadAsync(storageKey, cancellationToken);
            return opened is null ? null : opened with { ContentType = mimeType, FileName = fileName };
        }

        // D10: el tamaño sale del objeto, no de la fila: es lo que de verdad hay para servir. Un 416 no abre nada.
        if (await store.SizeOfAsync(storageKey, cancellationToken) is not { } total)
        {
            return null;
        }

        var range = MediaByteRange.Resolve(requested, total);
        if (!range.Satisfiable)
        {
            return new MediaStreamDto(Stream.Null, mimeType, 0, fileName, range);
        }

        var slice = await store.OpenReadRangeAsync(storageKey, range.From, range.To, cancellationToken);
        return slice is null ? null : slice with { ContentType = mimeType, FileName = fileName, Length = range.Length, Range = range };
    }
}
