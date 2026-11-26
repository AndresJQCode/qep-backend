using Bootstrapper.Realtime;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.Presence;

/// <summary>
/// The heartbeat of the app hub's member presence (spec 2026-11-22 D-1), calqued on <see cref="PresenceHeartbeatWorker"/>: every 30 s it
/// takes this pod's snapshot of connected members and, in one batch, rewrites each tenant's <c>qep:rt:online:{tenantId}</c> entries with
/// the current time, renews the <c>EXPIRE</c> and trims whoever did not beat inside the window. It brings back a member another pod
/// removed while they are still here, and fixes a write of <see cref="ValkeyAppMemberPresence"/> that was lost. Only registered with
/// <c>Valkey:Areas:Presence</c> on.
/// </summary>
internal sealed class AppPresenceHeartbeatWorker(AppHubConnections connections, ValkeyGateway gateway, TimeProvider time) : BackgroundService
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
            // Host shutdown.
        }
    }

    /// <summary>One beat, internal for the tests so no <see cref="PeriodicTimer"/> gets in the way. A failure is counted by the gateway.</summary>
    internal async Task BeatAsync(CancellationToken cancellationToken)
    {
        var snapshot = connections.Snapshot();
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
                    var key = ValkeyKeys.AppOnline(tenantId);
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
