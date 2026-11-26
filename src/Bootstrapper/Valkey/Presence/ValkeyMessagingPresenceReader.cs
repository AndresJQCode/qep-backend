using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// El lector del reporte de uso con <c>Valkey:Areas:Presence</c> prendido (spec 2026-11-13, D-2): un
/// <c>ZRANGEBYSCORE (ahora − 90) +inf</c> por cada tenant pedido, en un solo batch. Sin éxito —Valkey caído, lento o sin
/// conexión— loguea un <c>Warning</c> y devuelve el registro local: el reporte muestra sólo a los conectados a este pod, pero
/// responde. El <c>Warning</c> no lleva ids ni el host.
/// </summary>
internal sealed partial class ValkeyMessagingPresenceReader(
    ValkeyGateway gateway,
    LocalMessagingPresenceReader fallback,
    TimeProvider time,
    ILogger<ValkeyMessagingPresenceReader> logger)
    : IMessagingPresenceReader
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListConnectedAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }

        var cutoff = ValkeyPresence.CutoffOf(time.GetUtcNow());
        var attempt = await gateway.TryAsync(
            ValkeyArea.Presence,
            async database =>
            {
                var batch = database.CreateBatch();
                var pending = tenantIds
                    .Distinct()
                    .Select(tenantId => (TenantId: tenantId, Members: batch.SortedSetRangeByScoreAsync(
                        ValkeyKeys.Presence(tenantId), cutoff, double.PositiveInfinity, Exclude.Start)))
                    .ToArray();
                batch.Execute();
                await Task.WhenAll(pending.Select(entry => entry.Members)).ConfigureAwait(false);
                return ToConnected(pending.Select(entry => (entry.TenantId, entry.Members.Result)));
            },
            cancellationToken).ConfigureAwait(false);

        if (attempt is { Succeeded: true, Value: { } connected })
        {
            return connected;
        }

        LogFallback(logger);
        return await fallback.ListConnectedAsync(tenantIds, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<Guid, IReadOnlyList<Guid>> ToConnected(IEnumerable<(Guid TenantId, RedisValue[] Members)> results)
    {
        var connected = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var (tenantId, members) in results)
        {
            var memberIds = members
                .Select(member => Guid.TryParse((string?)member, out var memberId) ? memberId : Guid.Empty)
                .Where(memberId => memberId != Guid.Empty)
                .ToList();
            if (memberIds.Count > 0)
            {
                connected[tenantId] = memberIds;
            }
        }

        return connected;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Shared inbox presence unavailable; the usage report shows only this pod's connected members.")]
    private static partial void LogFallback(ILogger logger);
}
