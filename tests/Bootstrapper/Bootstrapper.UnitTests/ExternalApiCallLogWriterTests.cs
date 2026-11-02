using Bootstrapper.ApiKeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>Spec 2026-11-01 parte 1: el escritor que vacía la cola. Una fila que falla se loguea y no frena a las demás.</summary>
public sealed class ExternalApiCallLogWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiKeyRequest Row(string path) =>
        ApiKeyRequest.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), "ERP", "POST", path, 201, null, null, "t", 5, Now);

    [Fact]
    public async Task AFailingWriteIsLoggedAndTheWriterKeepsDraining()
    {
        var log = new RecordingLog(failOn: "/primera");
        await using var provider = new ServiceCollection().AddScoped<IApiKeyRequestLog>(_ => log).BuildServiceProvider();
        var logger = new CapturingLogger();
        var queue = new ExternalApiCallLogChannel();
        using var writer = new ExternalApiCallLogWriter(queue, provider.GetRequiredService<IServiceScopeFactory>(), logger);

        Assert.True(queue.TryEnqueue(Row("/primera")));
        Assert.True(queue.TryEnqueue(Row("/segunda")));
        await writer.StartAsync(Ct);
        var written = await log.WaitForAsync("/segunda");
        await writer.StopAsync(Ct);

        Assert.True(written);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ShutdownDrainsWhatIsQueued()
    {
        var log = new RecordingLog(failOn: null);
        await using var provider = new ServiceCollection().AddScoped<IApiKeyRequestLog>(_ => log).BuildServiceProvider();
        var queue = new ExternalApiCallLogChannel();
        using var writer = new ExternalApiCallLogWriter(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExternalApiCallLogWriter>.Instance);

        await writer.StartAsync(Ct);
        Assert.True(queue.TryEnqueue(Row("/uno")));
        Assert.True(queue.TryEnqueue(Row("/dos")));
        await writer.StopAsync(Ct);

        Assert.Equal(["/uno", "/dos"], log.Paths);
    }

    private sealed class RecordingLog(string? failOn) : IApiKeyRequestLog
    {
        private readonly List<string> _paths = [];

        public string[] Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        public Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken)
        {
            if (request.Path == failOn)
            {
                throw new InvalidOperationException("El registro falló a propósito.");
            }

            lock (_paths)
            {
                _paths.Add(request.Path);
            }

            return Task.CompletedTask;
        }

        public async Task<bool> WaitForAsync(string path)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Paths.Contains(path))
                {
                    return true;
                }

                await Task.Delay(20, Ct);
            }

            return false;
        }
    }

    private sealed class CapturingLogger : ILogger<ExternalApiCallLogWriter>
    {
        private readonly List<(LogLevel Level, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, exception));
            }
        }
    }
}
