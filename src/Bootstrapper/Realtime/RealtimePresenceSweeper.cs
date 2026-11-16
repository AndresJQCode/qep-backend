using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bootstrapper.Realtime;

/// <summary>
/// Plan N10: an entry nobody renews and nobody clears (its pod died, the network cut) would stay on screen until the next change. Every
/// <see cref="RealtimePresence.SweepInterval"/> each pod rereads the groups whose last photo it delivered was not empty and, if the
/// members changed, delivers the new photo to its own connections only (no NOTIFY: every pod sweeps its own). Worst case ≈ 45 s + 5 s.
/// </summary>
internal sealed partial class RealtimePresenceSweeper(
    IRealtimePresence presence,
    AppHubDispatcher dispatcher,
    IServiceScopeFactory scopes,
    ILogger<RealtimePresenceSweeper> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The realtime presence sweep failed; it runs again on the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RealtimePresence.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogSweepFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        var watched = dispatcher.PresenceToWatch();
        if (watched.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var names = scope.ServiceProvider.GetRequiredService<IRealtimeMemberNames>();
        foreach (var (tenantId, topic, delivered) in watched)
        {
            var current = await presence.ReadAsync(tenantId, topic, cancellationToken);
            if (current.Order().SequenceEqual(delivered.Select(member => member.MemberId).Order()))
            {
                continue;
            }

            var members = await RealtimePresenceNames.ComposeAsync(names, tenantId, current, cancellationToken);
            await dispatcher.DeliverPresenceAsync(tenantId, topic, members, cancellationToken);
        }
    }
}

/// <summary>The members of a <c>presenceChanged</c> payload: each present member with its visible name, or «Sin nombre».</summary>
internal static class RealtimePresenceNames
{
    // The same label as an unnamed member elsewhere in the console (SupportMemberDirectory, MembershipEndpoints).
    private const string NoName = "Sin nombre";

    public static async Task<IReadOnlyList<PresenceMember>> ComposeAsync(
        IRealtimeMemberNames names, Guid tenantId, IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
    {
        if (memberIds.Count == 0)
        {
            return [];
        }

        var found = await names.FindAsync(tenantId, memberIds, cancellationToken);
        return
        [
            .. memberIds.Select(memberId => new PresenceMember(
                memberId, found.TryGetValue(memberId, out var name) ? name : NoName, RealtimePresence.Composing)),
        ];
    }
}
