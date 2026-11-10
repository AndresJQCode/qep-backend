using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09, prueba 6 y D9: un <c>Sending</c> con el lease vencido nunca se reenvía; se cierra con lo que dice la tabla de
/// mensajes (Sent / Failed) o como <c>unknown_outcome</c>. La purga borra el adjunto y limpia la clave aunque el borrado falle.</summary>
public sealed class ScheduledMessageRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MessagingTestBed _bed = new();
    private readonly FakeScheduledMessageStore _store = new();

    private ScheduledMessageDispatcher Dispatcher() =>
        new(_store, _bed.Outbound, _bed.Store, _bed.Notifier, new FakeClock(_bed.Now));

    private ClaimedScheduledMessage Expired(string? mediaKey = null, int attempts = 2) =>
        new(Guid.CreateVersion7(), _bed.TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), MessageKind.Text, false, "Hola", null, mediaKey, null,
            null, null, null, Guid.CreateVersion7(), attempts, _bed.Now);

    private static ExistingOutbound Row(MessageStatus status) =>
        new(Guid.CreateVersion7(), status, DateTimeOffset.UnixEpoch, null, "Hola");

    [Theory]
    [InlineData(MessageStatus.Sent)]
    [InlineData(MessageStatus.Delivered)]
    [InlineData(MessageStatus.Read)]
    public async Task AnExpiredLeaseWithASentMessageBecomesSent(MessageStatus status)
    {
        var message = Expired();
        var row = Row(status);
        _bed.Outbound.Committed = row;
        _store.Expired.Enqueue([message]);

        var recovered = await Dispatcher().RecoverExpiredAsync(Ct);

        Assert.Equal(1, recovered);
        Assert.Equal(new CompletedSent(message.Id, 2, row.Id), Assert.Single(_store.Sent));
        Assert.Empty(_store.Failed);
        Assert.Single(_bed.Notifier.Events);
    }

    [Fact]
    public async Task AnExpiredLeaseWithAFailedMessageBecomesFailedRejected()
    {
        var message = Expired();
        _bed.Outbound.Committed = Row(MessageStatus.Failed);
        _store.Expired.Enqueue([message]);

        await Dispatcher().RecoverExpiredAsync(Ct);

        Assert.Equal(new CompletedFailed(message.Id, 2, MessagingErrorCodes.MessageRejected), Assert.Single(_store.Failed));
        Assert.Empty(_store.Sent);
        Assert.Single(_bed.Notifier.Events);
    }

    [Fact]
    public async Task AnExpiredLeaseWithoutAMessageIsUnknownOutcomeAndNeverRescheduled()
    {
        var message = Expired();
        _bed.Outbound.Committed = null;
        _store.Expired.Enqueue([message]);

        await Dispatcher().RecoverExpiredAsync(Ct);

        Assert.Equal(new CompletedFailed(message.Id, 2, MessagingErrorCodes.ScheduledUnknownOutcome), Assert.Single(_store.Failed));
        Assert.Empty(_store.Sent);
        Assert.Empty(_bed.Outbound.Claims);
    }

    [Fact]
    public async Task ALateCompletionAfterRecoveryWritesNothing()
    {
        _store.CompleteSucceeds = false;
        _bed.Outbound.Committed = Row(MessageStatus.Sent);
        _store.Expired.Enqueue([Expired()]);

        var recovered = await Dispatcher().RecoverExpiredAsync(Ct);

        Assert.Equal(0, recovered);
        Assert.Empty(_store.Sent);
        Assert.Empty(_bed.Notifier.Events);
    }

    [Fact]
    public async Task ATickClaimsDueRowsWithTheRealLease()
    {
        var message = Expired();
        _store.Due.Enqueue([message]);

        var claimed = await Dispatcher().ClaimDueAsync(Ct);

        Assert.Equal(message, Assert.Single(claimed));
        Assert.Equal(20, _store.LastDueTake);
        Assert.Equal(_bed.Now, _store.LastDueNow);
        Assert.Equal(TimeSpan.FromMinutes(10), _store.LastDueLease);
    }

    [Fact]
    public async Task RecoveryRenewsTheLeaseWithTheRealLength()
    {
        _store.Expired.Enqueue([]);

        await Dispatcher().RecoverExpiredAsync(Ct);

        Assert.Equal(20, _store.LastExpiredTake);
        Assert.Equal(TimeSpan.FromMinutes(10), _store.LastExpiredLease);
    }

    [Fact]
    public async Task PurgeDeletesAndClearsTheKeyEvenIfDeleteFails()
    {
        var first = new PurgeableScheduledMedia(_bed.TenantId, Guid.CreateVersion7(), "messaging/scheduled/a.jpg");
        var second = new PurgeableScheduledMedia(_bed.TenantId, Guid.CreateVersion7(), "messaging/scheduled/b.jpg");
        _store.Purgeable.AddRange([first, second]);
        _bed.Store.DeleteFailure = new InvalidOperationException("r2 down");

        var purged = await Dispatcher().PurgeAsync(Ct);

        Assert.Equal(2, purged);
        Assert.Equal([first.StorageKey, second.StorageKey], _bed.Store.Deleted);
        Assert.Equal([(first.TenantId, first.Id), (second.TenantId, second.Id)], _store.ClearedMediaKeys);
    }
}
