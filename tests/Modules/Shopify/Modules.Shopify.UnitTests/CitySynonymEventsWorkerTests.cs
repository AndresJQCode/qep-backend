using Modules.Shopify.Infrastructure.Workers;

namespace Modules.Shopify.UnitTests;

/// <summary>El sondeo de los eventos de sinónimos es costo en reposo contra una base compartida, pero lo espera una persona: la
/// consola del operador pide el primer avance de un sinónimo nuevo cada 60 s, y entre el evento y ese avance corre además el worker
/// que corrige. Se fija en 10 s, como el consumidor de eventos de cotización de develop, y una prueba lo sostiene.</summary>
public sealed class CitySynonymEventsWorkerTests
{
    [Fact]
    public void PollsEveryTenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(10), CitySynonymEventsWorker.PollInterval);
}
