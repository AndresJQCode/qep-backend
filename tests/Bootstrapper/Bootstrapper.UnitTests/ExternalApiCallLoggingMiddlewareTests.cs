using BuildingBlocks.Application;
using Bootstrapper.ApiKeys;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-01 parte 1, anti-objetivo: el registro no demora un envío. Kestrel espera los callbacks de <c>OnCompleted</c>
/// antes de leer la siguiente petición de la misma conexión, así que lo que el callback espere se le suma a la siguiente llamada
/// del integrador. Por eso estas pruebas disparan los callbacks a mano: con TestServer la respuesta sale antes de que corran, y
/// una escritura lenta en la base no se notaría.
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
        var queue = new ExternalApiCallLogChannel();
        var (context, callbacks) = ExternalContext();
        var middleware = NewMiddleware(provider, queue);

        await middleware.InvokeAsync(context, new FixedClock());
        var completion = callbacks.RunAsync();

        // La prueba falla si el callback espera a la escritura: nunca se libera hasta que el finally la suelta.
        try
        {
            var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(2), Ct));
            Assert.Same(completion, finished);
        }
        finally
        {
            log.Release();
        }
    }

    [Fact]
    public async Task AFullQueueDropsTheRowWithoutBlocking()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var queue = new ExternalApiCallLogChannel(capacity: 1);
        var (first, firstCallbacks) = ExternalContext();
        var (second, secondCallbacks) = ExternalContext();
        var middleware = NewMiddleware(provider, queue);

        await middleware.InvokeAsync(first, new FixedClock());
        await middleware.InvokeAsync(second, new FixedClock());
        await firstCallbacks.RunAsync();
        await secondCallbacks.RunAsync().WaitAsync(TimeSpan.FromSeconds(2), Ct);

        Assert.True(queue.Reader.TryRead(out _));
        Assert.False(queue.Reader.TryRead(out _));
    }

    private static ExternalApiCallLoggingMiddleware NewMiddleware(IServiceProvider provider, ExternalApiCallLogChannel queue) =>
        new(
            context =>
            {
                ExternalCallNote.Find(context)!.Identify(KeyId, "ERP", TenantId);
                context.Response.StatusCode = 201;
                return Task.CompletedTask;
            },
            queue,
            NullLogger<ExternalApiCallLoggingMiddleware>.Instance);

    private static (DefaultHttpContext Context, CapturedCallbacks Callbacks) ExternalContext()
    {
        var callbacks = new CapturedCallbacks();
        var context = new DefaultHttpContext();
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

        public void Release() => _gate.TrySetResult();

        public Task RecordAsync(ApiKeyRequest request, CancellationToken cancellationToken) => _gate.Task;
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
}
