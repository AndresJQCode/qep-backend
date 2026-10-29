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

    public async Task<IReadOnlyList<Guid>> FindOrderAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
        await dbContext.QuickReplyOrders.AsNoTracking()
            .Where(order => order.TenantId == tenantId && order.MemberId == memberId)
            .Select(order => order.QuickReplyIds)
            .SingleOrDefaultAsync(cancellationToken) ?? [];

    // Spec 2026-10-29: upsert por SQL y no Add/Update con tracking. Dos primeros guardados del mismo miembro a la vez (dos pestañas)
    // chocarían con la PK, y el contrato es «gana la última escritura», no un 412. La transacción explícita (como SwapDefaultAsync) lleva
    // en el mismo commit la auditoría que el handler dejó pendiente.
    public async Task SaveOrderAsync(
        Guid tenantId, Guid memberId, IReadOnlyList<Guid> quickReplyIds, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quickReplyIds);
        var ids = quickReplyIds.ToArray();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO messaging.quick_reply_orders (tenant_id, member_id, quick_reply_ids, updated_at)
             VALUES ({tenantId}, {memberId}, {ids}, {savedAt})
             ON CONFLICT (tenant_id, member_id) DO UPDATE SET quick_reply_ids = EXCLUDED.quick_reply_ids, updated_at = EXCLUDED.updated_at
             """,
            cancellationToken);
        await MessagingSaveChanges.SaveAsync(dbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
