using Microsoft.Extensions.Hosting;
using Modules.Messaging.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// El latido de la presencia compartida (spec 2026-11-13, D-2): cada 30 s toma la foto local del pod y, en un solo batch,
/// por cada tenant con alguien conectado acá manda el <c>ZADD</c> de todos sus miembros con la hora actual, renueva el
/// <c>EXPIRE</c> y recorta con <c>ZREMRANGEBYSCORE</c> a quien no latió dentro de la ventana, para que el set no crezca. Es lo
/// que devuelve a un miembro que otro pod quitó mientras seguía conectado acá, y lo que corrige una escritura de
/// <see cref="ValkeyMessagingPresence"/> que se perdió. Sólo se registra con <c>Valkey:Areas:Presence</c> prendido.
/// </summary>
internal sealed class PresenceHeartbeatWorker(MessagingPresence local, ValkeyGateway gateway, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ValkeyPresence.HeartbeatInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await BeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado del host.
        }
    }

    /// <summary>Un latido: internal para las pruebas, sin el <see cref="PeriodicTimer"/> de por medio. Un fallo lo cuenta el gateway.</summary>
    internal async Task BeatAsync(CancellationToken cancellationToken)
    {
        var snapshot = local.Snapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var score = ValkeyPresence.ScoreOf(now);
        var cutoff = ValkeyPresence.CutoffOf(now);
        await gateway.TryAsync(
            ValkeyArea.Presence,
            async database =>
            {
                var batch = database.CreateBatch();
                var pending = new List<Task>(snapshot.Count * 3);
                foreach (var (tenantId, memberIds) in snapshot)
                {
                    var key = ValkeyKeys.Presence(tenantId);
                    var entries = memberIds.Select(memberId => new SortedSetEntry(memberId.ToString("D"), score)).ToArray();
                    pending.Add(batch.SortedSetAddAsync(key, entries));
                    pending.Add(batch.KeyExpireAsync(key, ValkeyPresence.KeyTtl));
                    pending.Add(batch.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, cutoff, Exclude.Stop));
                }

                batch.Execute();
                await Task.WhenAll(pending).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
