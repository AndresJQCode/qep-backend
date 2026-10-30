using BuildingBlocks.Domain;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-29 (historial por coexistencia): el estado de la importación por conexión, su precedencia, la ventana de
/// 24 horas para pedirla y el reintento.</summary>
public sealed class HistorySyncTests
{
    private static readonly DateTimeOffset ConnectedAt = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static HistorySync New() => HistorySync.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), ConnectedAt, ConnectedAt);

    [Fact]
    public void ANewSyncIsPendingWithTheDeadline24HoursAfterTheConnection()
    {
        var sync = New();

        Assert.Equal(HistorySyncStatus.Pending, sync.StatusAt(ConnectedAt));
        Assert.Equal(ConnectedAt.AddHours(24), sync.Deadline);
        Assert.Equal(HistorySyncRequestState.Pending, sync.ContactsState);
        Assert.Equal(HistorySyncRequestState.Pending, sync.HistoryState);
        Assert.Equal(1, sync.Version);
        Assert.Equal(ConnectedAt, sync.UpdatedAt);
    }

    // Una fila por paso de la precedencia: Declined → Completed → InProgress → Expired → Failed → Pending → Requested.
    [Theory]
    [InlineData(true, true, true, "Failed", "Failed", 1.0, HistorySyncStatus.Declined)]
    [InlineData(false, true, true, "Failed", "Failed", 1.0, HistorySyncStatus.Completed)]
    [InlineData(false, false, true, "Failed", "Failed", 30.0, HistorySyncStatus.InProgress)]
    [InlineData(false, false, false, "Requested", "Failed", 24.0, HistorySyncStatus.Expired)]
    [InlineData(false, false, false, "Pending", "Requested", 25.0, HistorySyncStatus.Expired)]
    [InlineData(false, false, false, "Requested", "Failed", 23.0, HistorySyncStatus.Failed)]
    [InlineData(false, false, false, "Pending", "Failed", 1.0, HistorySyncStatus.Failed)]
    [InlineData(false, false, false, "Requested", "Pending", 1.0, HistorySyncStatus.Pending)]
    [InlineData(false, false, false, "Requested", "Requested", 1.0, HistorySyncStatus.Requested)]
    [InlineData(false, false, false, "Requested", "Requested", 30.0, HistorySyncStatus.Requested)]
    public void StatusPrecedence(
        bool declined, bool completed, bool progress, string contacts, string history, double hoursAfter, HistorySyncStatus expected)
    {
        var sync = HistorySync.ForTests(
            New(),
            Enum.Parse<HistorySyncRequestState>(contacts),
            Enum.Parse<HistorySyncRequestState>(history),
            historyProgress: progress ? 40 : null,
            declinedAt: declined ? ConnectedAt.AddMinutes(5) : null,
            completedAt: completed ? ConnectedAt.AddMinutes(10) : null);

        Assert.Equal(expected, sync.StatusAt(ConnectedAt.AddHours(hoursAfter)));
    }

    [Fact]
    public void RetryMovesFailedToPendingOnlyBeforeTheDeadline()
    {
        var sync = HistorySync.ForTests(New(), HistorySyncRequestState.Requested, HistorySyncRequestState.Failed, historyErrorCode: "graph_rejected");
        var at = ConnectedAt.AddHours(1);

        Assert.True(sync.CanRetryAt(at));
        sync.Retry(at);

        Assert.Equal(HistorySyncRequestState.Pending, sync.HistoryState);
        Assert.Null(sync.HistoryErrorCode);
        Assert.Equal(HistorySyncRequestState.Requested, sync.ContactsState);
        Assert.Equal(2, sync.Version);
        Assert.Equal(at, sync.UpdatedAt);

        var late = HistorySync.ForTests(New(), HistorySyncRequestState.Requested, HistorySyncRequestState.Failed, historyErrorCode: "graph_rejected");
        Assert.False(late.CanRetryAt(ConnectedAt.AddHours(24)));
        var error = Assert.ThrowsAny<DomainException>(() => late.Retry(ConnectedAt.AddHours(24)));
        Assert.Equal("messaging.history_sync.retry_not_allowed", error.Code);
        Assert.Equal(HistorySyncRequestState.Failed, late.HistoryState);
        Assert.Equal(1, late.Version);
    }

    [Fact]
    public void ADeclinedOrFullyRequestedSyncCannotBeRetried()
    {
        var declined = HistorySync.ForTests(New(), HistorySyncRequestState.Failed, HistorySyncRequestState.Failed, declinedAt: ConnectedAt);
        var requested = HistorySync.ForTests(New(), HistorySyncRequestState.Requested, HistorySyncRequestState.Requested);

        Assert.False(declined.CanRetryAt(ConnectedAt.AddHours(1)));
        Assert.False(requested.CanRetryAt(ConnectedAt.AddHours(1)));
    }

    [Fact]
    public void ResolveAndReopenClearHistoryManaged()
    {
        var conversation = Conversation.WithHistoryManagedForTests(
            Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", ConnectedAt));
        Assert.True(conversation.HistoryManaged);

        conversation.Resolve(ConnectedAt.AddMinutes(1));
        Assert.False(conversation.HistoryManaged);

        var reopened = Conversation.WithHistoryManagedForTests(conversation);
        reopened.Reopen(ConnectedAt.AddMinutes(2));
        Assert.False(reopened.HistoryManaged);
    }

    // Regla R9: asignarla (tomarla o transferirla) también la saca de la regla de la importación.
    [Fact]
    public void TakingOrTransferringClearsHistoryManaged()
    {
        var taken = Conversation.WithHistoryManagedForTests(
            Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", ConnectedAt));
        taken.Take(Guid.CreateVersion7(), ConnectedAt.AddMinutes(1));
        Assert.False(taken.HistoryManaged);

        var transferred = Conversation.WithHistoryManagedForTests(
            Conversation.Start(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "573001234567", "Laura", ConnectedAt));
        transferred.TransferTo(Guid.CreateVersion7(), Guid.CreateVersion7(), ConnectedAt.AddMinutes(1));
        Assert.False(transferred.HistoryManaged);
    }
}
