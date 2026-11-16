using Bootstrapper.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>Block B review: the listener "never brings the host down". A missing connection string is one more failure to log and retry
/// with backoff, not an exception out of <c>ExecuteAsync</c> that stops the host.</summary>
public sealed class RealtimeEventsListenerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMissingConnectionStringIsLoggedAndRetriedWithoutStoppingTheHost()
    {
        var logger = new FirstWarningLogger();
        using var listener = new RealtimeEventsListener(
            new ConfigurationBuilder().Build(),
            new AppHubDispatcher(new RecordingHubContext(), new AppHubConnections()),
            logger);

        await listener.StartAsync(Ct);
        var warning = await logger.FirstWarning.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.IsType<InvalidOperationException>(warning);
        Assert.False(listener.ExecuteTask!.IsFaulted);
        await listener.StopAsync(Ct);
        Assert.True(listener.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class FirstWarningLogger : ILogger<RealtimeEventsListener>
    {
        private readonly TaskCompletionSource<Exception?> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Exception?> FirstWarning => _first.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                _first.TrySetResult(exception);
            }
        }
    }
}
