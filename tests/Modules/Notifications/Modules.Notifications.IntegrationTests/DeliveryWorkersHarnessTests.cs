using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Notifications.Infrastructure.Messaging;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>El interruptor del harness: sin él, cada prueba que llama a DrainAsync competiría con el
/// bucle de correo del host, que drena los trece consumidores (spec 2026-11-13, D-7).</summary>
public sealed class DeliveryWorkersHarnessTests
{
    [Fact]
    public async Task TheTestHostLeavesTheDeliveryWorkersOutByDefault()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString());

        Assert.DoesNotContain(
            factory.Services.GetServices<IHostedService>(),
            service => IsDeliveryWorker(service.GetType()));
    }

    [Fact]
    public async Task TheSwitchPutsTheDeliveryLoopBackWithItsThirteenConsumers()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), runDeliveryWorkers: true);

        var loop = Assert.Single(factory.Services.GetServices<IHostedService>(), service => IsDeliveryWorker(service.GetType()));
        Assert.IsType<NotificationsDeliveryWorker>(loop);
        Assert.Equal(13, factory.Services.GetServices<OutboxDeliveryWorker>().Count());
    }
}
