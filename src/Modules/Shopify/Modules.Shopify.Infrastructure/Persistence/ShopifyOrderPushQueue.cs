using Microsoft.EntityFrameworkCore;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.Infrastructure.Persistence;

/// <summary>Spec 2026-11-14 §5: la cola de <see cref="ShopifyOrderPushWorker"/>, como la de los trabajos de corrección de ciudad.
/// El reclamo y la espera no tocan <c>version</c>: una corrección que se fusiona con el pendiente mientras se manda no choca con
/// ellos. Cerrar el envío sí la sube (<see cref="MarkFailedAsync"/>, review B3-B): una corrección que leyó el pendiente y se
/// fusiona con él después choca (412) en vez de responder 200 sobre un envío que nadie va a mandar. Es terminal, así que no
/// choca con ningún reclamo.</summary>
internal sealed class ShopifyOrderPushQueue(ShopifyDbContext dbContext) : IShopifyOrderPushQueue
{
    private static readonly string Pending = nameof(ShopifyOrderPushStatus.Pending);

    private static readonly string Failed = nameof(ShopifyOrderPushStatus.Failed);

    public async Task<IReadOnlyList<Guid>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await dbContext.OrderPushes.AsNoTracking()
            .Where(push => push.Status == ShopifyOrderPushStatus.Pending && (push.LeaseUntil == null || push.LeaseUntil < now))
            .OrderBy(push => push.Id)
            .Select(push => push.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    // SKIP LOCKED: una réplica que está cerrando el envío tiene su fila tomada por el UPDATE de su guardado; esperarla no sirve. El
    // lease es el de envío en vuelo (review Minor 2); la espera de la escalera la escribe ScheduleRetryAsync al terminar el intento.
    public async Task<int?> TryClaimAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var leaseUntil = now + ShopifyOrderPushWorker.InFlightLease;
        var rows = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            WITH claimable AS (
                SELECT id FROM shopify.order_pushes
                 WHERE id = {id} AND status = {Pending} AND (lease_until IS NULL OR lease_until < {now})
                   FOR UPDATE SKIP LOCKED)
            UPDATE shopify.order_pushes push
               SET lease_until = {leaseUntil},
                   attempts = push.attempts + 1,
                   updated_at = {now}
              FROM claimable
             WHERE push.id = claimable.id
            RETURNING push.attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0].Attempts : null;
    }

    public Task ScheduleRetryAsync(Guid id, DateTimeOffset until, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.order_pushes
               SET lease_until = {until}
             WHERE id = {id} AND status = {Pending}
            """,
            cancellationToken);

    public Task MarkFailedAsync(Guid id, DateTimeOffset now, string code, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE shopify.order_pushes
               SET status = {Failed}, failure_code = {code}, lease_until = NULL, updated_at = {now}, version = version + 1
             WHERE id = {id} AND status = {Pending}
            """,
            cancellationToken);

    private sealed record ClaimRow(int Attempts);
}
