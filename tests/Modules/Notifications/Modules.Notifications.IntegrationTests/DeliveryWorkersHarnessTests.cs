using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>El interruptor del harness: sin él, cada prueba que llama a DrainAsync competiría con los
/// trece workers del host.</summary>
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
    public async Task TheSwitchPutsTheThirteenWorkersBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new NotificationsApiFactory(database.GetConnectionString(), runDeliveryWorkers: true);

        Assert.Equal(13, factory.Services.GetServices<IHostedService>()
            .Count(service => IsDeliveryWorker(service.GetType())));
    }
}
