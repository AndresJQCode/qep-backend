using Modules.Storage.Infrastructure.PaymentProofs;

namespace Modules.Storage.UnitTests;

/// <summary>El sondeo del movimiento de comprobantes es costo en reposo contra una base compartida: nadie espera
/// el movimiento de forma interactiva, así que el intervalo se fija alto y una prueba lo sostiene.</summary>
public sealed class PaymentProofMoveWorkerTests
{
    [Fact]
    public void PollsEveryFifteenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), PaymentProofMoveWorker.PollInterval);
}
