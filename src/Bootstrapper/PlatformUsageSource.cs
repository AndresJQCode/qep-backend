using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Adapta Messaging y su presencia al puerto <see cref="IPlatformUsageSource"/> de Tenancy, que no los referencia. Es SQL crudo
/// sobre la conexión de <see cref="MessagingDbContext"/> y conoce por nombre <c>tenancy.tenants</c>, <c>messaging.messages</c> y
/// <c>messaging.conversations</c>. Una conversación cuenta una sola vez por canal si tuvo al menos un entrante en vivo
/// (<c>direction = 1</c>, no importado) desde el corte, buscado por tenant (un LATERAL por tenant) para que use
/// <c>IX_messages_tenant_created</c>: sin el <c>tenant_id</c> el filtro por fecha no puede buscar en el índice; el canal sale de la conversación y no de la conexión, así que una
/// conexión de Instagram borrada no le quita lo que ya contó. Un tenant sin conversaciones sigue en la lista, en cero.
///
/// Renombrar una columna de esas tablas compila y deja <c>/operator/usage</c> respondiendo 500 (<c>42703</c>). Lo amarra
/// <c>PlatformUsageApiTests</c> (Messaging.IntegrationTests), que corre contra el esquema migrado. La presencia es por proceso
/// (<see cref="IMessagingPresence"/>): con más de una réplica cada una vería sólo a sus conectados.
/// </summary>
internal sealed class PlatformUsageSource(
    MessagingDbContext messaging, IMessagingPresence presence, IMessagingMemberNames memberNames) : IPlatformUsageSource
{
    // Misma redacción que MessageMapping.DeletedMemberName (D-M20), que es internal de Messaging.Application.
    private const string DeletedMemberName = "Miembro eliminado";

    public async Task<IReadOnlyList<PlatformUsageTenantRow>> ListAsync(DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        var rows = await messaging.Database.SqlQuery<UsageRow>($"""
            SELECT t.id AS "TenantId", t.display_name AS "Name", t.created_at AS "CreatedAt",
                   (count(*) FILTER (WHERE c.channel = 'WhatsApp'))::int AS "WhatsApp",
                   (count(*) FILTER (WHERE c.channel = 'Instagram'))::int AS "Instagram"
            FROM tenancy.tenants t
            LEFT JOIN LATERAL (
                SELECT DISTINCT m.conversation_id
                FROM messaging.messages m
                WHERE m.tenant_id = t.id AND m.direction = 1 AND NOT m.imported AND m.created_at >= {startUtc.ToUniversalTime()}
            ) a ON true
            LEFT JOIN messaging.conversations c ON c.id = a.conversation_id
            GROUP BY t.id, t.display_name, t.created_at
            """).ToListAsync(cancellationToken);

        var snapshot = presence.Snapshot();
        var known = rows.Select(row => row.TenantId).ToHashSet();
        var names = new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>();
        foreach (var (tenantId, memberIds) in snapshot)
        {
            // Un tenant de la presencia sin fila (borrado, o ajeno) se ignora: no hay dónde mostrarlo.
            if (known.Contains(tenantId) && memberIds.Count > 0)
            {
                names[tenantId] = await memberNames.FindAsync(tenantId, memberIds, cancellationToken);
            }
        }

        return rows
            .Select(row => new PlatformUsageTenantRow(
                row.TenantId, row.Name, row.CreatedAt, row.WhatsApp, row.Instagram, ConnectedOf(row.TenantId, snapshot, names)))
            .ToArray();
    }

    private static PlatformConnectedMemberDto[] ConnectedOf(
        Guid tenantId,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> snapshot,
        Dictionary<Guid, IReadOnlyDictionary<Guid, string>> names)
    {
        if (!snapshot.TryGetValue(tenantId, out var memberIds) || !names.TryGetValue(tenantId, out var tenantNames))
        {
            return [];
        }

        // El snapshot no trae orden: se ordena por nombre para que la lista no baile entre consultas.
        return memberIds
            .Select(id => new PlatformConnectedMemberDto(id, tenantNames.GetValueOrDefault(id, DeletedMemberName)))
            .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class UsageRow
    {
        public Guid TenantId { get; init; }

        public string Name { get; init; } = string.Empty;

        public DateTimeOffset CreatedAt { get; init; }

        public int WhatsApp { get; init; }

        public int Instagram { get; init; }
    }
}
