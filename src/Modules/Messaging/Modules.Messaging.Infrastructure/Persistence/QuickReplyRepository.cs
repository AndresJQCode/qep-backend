using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-12 §3.1: el agregado con tracking (la versión es el token de concurrencia, como la campaña).</summary>
internal sealed class QuickReplyRepository(MessagingDbContext dbContext) : IQuickReplyRepository
{
    public Task<QuickReply?> FindAsync(Guid tenantId, Guid quickReplyId, CancellationToken cancellationToken) =>
        dbContext.QuickReplies.SingleOrDefaultAsync(quickReply => quickReply.TenantId == tenantId && quickReply.Id == quickReplyId, cancellationToken);

    public async Task<IReadOnlyList<QuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.QuickReplies.AsNoTracking().Where(quickReply => quickReply.TenantId == tenantId).ToListAsync(cancellationToken);
        // D-Q7: orden ordinal y no la collation de la base, que depende de cómo se creó el clúster. Un tenant tiene decenas.
        return rows.OrderBy(quickReply => quickReply.Shortcut, StringComparer.Ordinal).ToArray();
    }

    public void Add(QuickReply quickReply) => dbContext.QuickReplies.Add(quickReply);

    public void Remove(QuickReply quickReply) => dbContext.QuickReplies.Remove(quickReply);
}
