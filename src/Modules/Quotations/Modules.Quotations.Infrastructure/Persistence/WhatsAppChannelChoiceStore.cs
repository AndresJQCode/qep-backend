using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class WhatsAppChannelChoiceStore(QuotationsDbContext dbContext) : IWhatsAppChannelChoiceStore
{
    public async Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await dbContext.TenantWhatsAppChannels
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .Select(row => (Guid?)row.ConnectionId)
            .SingleOrDefaultAsync(cancellationToken);

    // Un solo statement, sin leer antes: el WHERE del DO UPDATE es la comparación con lo que el llamador vio. Con
    // `previous = null` la comparación es `= NULL`, que nunca es verdadera, así que una fila existente no se toca.
    // SQL crudo en la conexión del DbContext: queda dentro de la transacción del llamador (con el `Sent`).
    public async Task<bool> RememberAsync(
        Guid tenantId,
        Guid connectionId,
        Guid? previous,
        Guid chosenByMemberId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var written = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.tenant_whatsapp_channels (tenant_id, connection_id, chosen_at, chosen_by_member_id)
            VALUES ({tenantId}, {connectionId}, {now}, {chosenByMemberId})
            ON CONFLICT (tenant_id) DO UPDATE SET
                connection_id = EXCLUDED.connection_id,
                chosen_at = EXCLUDED.chosen_at,
                chosen_by_member_id = EXCLUDED.chosen_by_member_id
            WHERE quotations.tenant_whatsapp_channels.connection_id = {previous}
            """,
            cancellationToken);
        return written > 0;
    }

    public async Task ReplaceAsync(
        Guid tenantId,
        Guid connectionId,
        Guid chosenByMemberId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.tenant_whatsapp_channels (tenant_id, connection_id, chosen_at, chosen_by_member_id)
            VALUES ({tenantId}, {connectionId}, {now}, {chosenByMemberId})
            ON CONFLICT (tenant_id) DO UPDATE SET
                connection_id = EXCLUDED.connection_id,
                chosen_at = EXCLUDED.chosen_at,
                chosen_by_member_id = EXCLUDED.chosen_by_member_id
            """,
            cancellationToken);
}
