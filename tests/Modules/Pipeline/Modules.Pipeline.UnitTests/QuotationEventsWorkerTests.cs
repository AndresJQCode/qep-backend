using Modules.Pipeline.Infrastructure.Messaging;

namespace Modules.Pipeline.UnitTests;

/// <summary>El sondeo de eventos de cotización es costo en reposo contra una base compartida: el tablero refleja el
/// cambio dentro del intervalo y nadie lo espera de forma interactiva, así que se fija en 10 s y una prueba lo sostiene.</summary>
public sealed class QuotationEventsWorkerTests
{
    [Fact]
    public void PollsEveryTenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(10), QuotationEventsWorker.PollInterval);
}
