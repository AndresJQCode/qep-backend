using Bootstrapper.ApiKeys;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-01 parte 1, anti-objetivo: el registro no demora un envío. Kestrel espera los callbacks de <c>OnCompleted</c>
/// antes de leer la siguiente petición de la misma conexión, así que lo que el callback espere se le suma a la siguiente llamada
/// del integrador. Por eso estas pruebas disparan los callbacks a mano: con TestServer la respuesta sale antes de que corran, y
/// una escritura lenta en la base no se notaría. El proveedor se pone también en <c>RequestServices</c>, como en Kestrel, para que
/// una escritura vuelta a meter en el callback tropiece con el registro bloqueado.
/// </summary>
public sealed class ExternalApiCallLoggingMiddlewareTests
{
    private static readonly Guid KeyId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASlowLogWriteDoesNotHoldTheCompletionCallbacks()
    {
        var log = new BlockingLog();
        await using var provider = new ServiceCollection().AddScoped<IApiKeyRequestLog>(_ => log).BuildServiceProvider();
        var channel = new ExternalApiCallLogChannel();
        using var writer = new ExternalApiCallLogWriter(channel, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExternalApiCallLogWriter>.Instance);
        var (context, callbacks) = ExternalContext(provider);
        var middleware = NewMiddleware(channel, new CapturingLogger());
        await writer.StartAsync(Ct);

        try
        {
            await middleware.InvokeAsync(context, new FixedClock());
            var completion = callbacks.RunAsync();

            // Primero se espera a que el escritor esté dentro de la escritura bloqueada; después, que el callback termine con ella retenida.
            await log.Entered.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(2), Ct));
            Assert.Same(completion, finished);
            Assert.False(log.Released);
        }
        finally
        {
            log.Release();
            await writer.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task AFullQueueDropsTheRowWithoutBlockingAndWarns()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var channel = new ExternalApiCallLogChannel(capacity: 1);
        var logger = new CapturingLogger();
        var (first, firstCallbacks) = ExternalContext(provider);
        var (second, secondCallbacks) = ExternalContext(provider);
        var middleware = NewMiddleware(channel, logger);

        await middleware.InvokeAsync(first, new FixedClock());
        await middleware.InvokeAsync(second, new FixedClock());
        await firstCallbacks.RunAsync();
        await secondCallbacks.RunAsync().WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.True(channel.Reader.TryRead(out _));
        Assert.False(channel.Reader.TryRead(out _));
        Assert.Single(logger.Levels, LogLevel.Warning);
    }

    private static ExternalApiCallLoggingMiddleware NewMiddleware(ExternalApiCallLogChannel channel, ILogger<ExternalApiCallLoggingMiddleware> logger) =>
        new(
            context =>
            {
                ExternalCallNote.Find(context)!.Identify(KeyId, "ERP", TenantId);
                context.Response.StatusCode = 201;
                return Task.CompletedTask;
            },
            channel,
            logger);

    private static (DefaultHttpContext Context, CapturedCallbacks Callbacks) ExternalContext(IServiceProvider provider)
    {
        var callbacks = new CapturedCallbacks();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Features.Set<IHttpResponseFeature>(callbacks);
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/external/whatsapp/messages";
        return (context, callbacks);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class BlockingLog : IApiKeyRequestLog
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public bool Released => _gate.Task.IsCompleted;

        public void Release() => _gate.TrySetResult();

        public Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            return _gate.Task;
        }
    }

    /// <summary>Guarda los callbacks de <c>OnCompleted</c> para dispararlos como lo hace Kestrel, uno tras otro y esperando cada uno.</summary>
    private sealed class CapturedCallbacks : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

        public override void OnCompleted(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));

        public async Task RunAsync()
        {
            foreach (var (callback, state) in _callbacks)
            {
                await callback(state);
            }
        }
    }

    private sealed class CapturingLogger : ILogger<ExternalApiCallLoggingMiddleware>
    {
        private readonly List<LogLevel> _levels = [];

        public IReadOnlyList<LogLevel> Levels
        {
            get
            {
                lock (_levels)
                {
                    return [.. _levels];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_levels)
            {
                _levels.Add(logLevel);
            }
        }
    }
}
