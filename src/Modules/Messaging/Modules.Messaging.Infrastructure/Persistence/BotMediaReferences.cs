using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

// Spec 2026-11-18, regla 2 del owner: los mensajes de bot que muestran un archivo de chatbot. LINQ sobre el mapeo, no SQL crudo: renombrar
// la columna rompe el mapeo, no el barrido en silencio. message_media no lleva tenant: va por el mensaje. IX_message_media_storage_file
// (parcial, sólo filas de bot) hace que la pregunta no recorra toda la tabla de adjuntos.
internal sealed class BotMediaReferences(MessagingDbContext dbContext) : IBotMediaReferences
{
    public async Task<IReadOnlySet<Guid>> FindReferencedAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = fileIds.ToList();
        var referenced = await dbContext.Media.AsNoTracking()
            .Where(media => media.StorageFileId != null && ids.Contains(media.StorageFileId.Value))
            .Join(
                dbContext.Messages.AsNoTracking().Where(message => message.TenantId == tenantId),
                media => media.MessageId,
                message => message.Id,
                (media, _) => media.StorageFileId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return referenced.ToHashSet();
    }
}
