using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.UnreferencedFiles;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Plan 2026-11-18, Task 11: un solo worker diario corre todos los barridos de archivos sin uso, cada uno en su propio <c>try</c>. Un barrido
/// que falla (Messaging caído para las plantillas) no deja sin correr al de los chatbots.
/// </summary>
public sealed class UnreferencedFileSweepWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWorkerRunsTheChatbotSweepWhenTheTemplateSweepThrows()
    {
        var template = new ScriptedSweep(FileOwnerType.MessageTemplate, fails: true);
        var chatbot = new ScriptedSweep(FileOwnerType.ChatbotMedia, fails: false);
        var services = new ServiceCollection()
            .AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero)))
            .AddScoped<IUnreferencedFileSweep>(_ => template)
            .AddScoped<IUnreferencedFileSweep>(_ => chatbot)
            .BuildServiceProvider();
        using var worker = new UnreferencedFileSweepWorker(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UnreferencedFileSweepWorker>.Instance)
        {
            InitialDelay = TimeSpan.FromMilliseconds(10),
        };

        await worker.StartAsync(Ct);
        // Espera la señal de la corrida, no un tiempo fijo: el plazo sólo corta una prueba colgada.
        await chatbot.Ran.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await worker.StopAsync(Ct);

        Assert.Equal(1, template.Runs);
        Assert.Equal(1, chatbot.Runs);
    }

    private sealed class ScriptedSweep(FileOwnerType ownerType, bool fails) : IUnreferencedFileSweep
    {
        private readonly TaskCompletionSource _ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public FileOwnerType OwnerType => ownerType;

        public Task Ran => _ran.Task;

        public int Runs => _runs;

        public Task<int> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            _ran.TrySetResult();
            return fails ? throw new InvalidOperationException("Simulated sweep failure.") : Task.FromResult(0);
        }
    }
}
