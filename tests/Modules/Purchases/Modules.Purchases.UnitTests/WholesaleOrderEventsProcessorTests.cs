using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modules.Purchases.Infrastructure;
using Modules.Purchases.Infrastructure.Messaging;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Revisión del bloque C. I1: las pruebas de integración sacan el worker del host buscando su descriptor por
/// <c>ImplementationType</c> (como Pipeline); si el registro pasara a una fábrica, ese filtro no lo encontraría y el worker
/// competiría con la prueba. M1: un evento que sigue esperando el envío (<c>RetryLater</c>) sube a Warning pasado el umbral.
/// </summary>
public sealed class WholesaleOrderEventsProcessorTests
{
    [Fact]
    public void TheWorkerIsRegisteredByTypeSoTheTestHostCanRemoveIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:QepDatabase"] = "Host=localhost" })
            .Build();
        var services = new ServiceCollection().AddPurchasesInfrastructure(configuration);

        var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList();

        var worker = Assert.Single(hosted, descriptor => descriptor.ImplementationType == typeof(WholesaleOrderEventsWorker));
        Assert.Null(worker.ImplementationFactory);
    }

    [Theory]
    [InlineData(1, LogLevel.Information)]
    [InlineData(WholesaleOrderEventsProcessor.RetryLaterWarningAfterAttempts, LogLevel.Information)]
    [InlineData(WholesaleOrderEventsProcessor.RetryLaterWarningAfterAttempts + 1, LogLevel.Warning)]
    public void ARetryLaterIsAWarningOnlyPastTheThreshold(int attempt, LogLevel expected) =>
        Assert.Equal(expected, WholesaleOrderEventsProcessor.RetryLaterLevel(attempt));
}
