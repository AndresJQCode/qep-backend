using Modules.Notifications.Infrastructure.Messaging;

namespace Modules.Notifications.UnitTests;

/// <summary>El sondeo de los workers de correo es costo en reposo contra una base compartida: nadie espera un
/// correo de forma interactiva, así que el intervalo se fija alto y una prueba lo sostiene.</summary>
public sealed class OutboxDeliveryWorkerTests
{
    [Fact]
    public void PollsEveryFifteenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), OutboxDeliveryWorker.PollInterval);
}
