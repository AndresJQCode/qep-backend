using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>§8.6: el tenant va en el filtro; un id de otro tenant no se distingue de uno que no existe.</summary>
internal sealed class MediaReads(MessagingDbContext dbContext) : IMediaReads
{
    public Task<StoredMedia?> FindStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken) =>
        dbContext.Messages.AsNoTracking()
            .Where(message => message.TenantId == tenantId && message.Id == messageId && message.Media != null && message.Media.StoredAt != null && message.Media.StorageKey != null)
            .Select(message => new StoredMedia(message.Media!.StorageKey!, message.Media.MimeType, message.Media.FileName, message.Media.SizeBytes))
            .SingleOrDefaultAsync(cancellationToken);
}
