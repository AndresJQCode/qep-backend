using Modules.Audit.Infrastructure.Messaging;

namespace Modules.Audit.UnitTests;

/// <summary>El sondeo de la proyección es costo en reposo contra una base compartida: nadie espera la auditoría
/// operativa de forma interactiva, así que el intervalo se fija alto y una prueba lo sostiene.</summary>
public sealed class AuditProjectionWorkerTests
{
    [Fact]
    public void PollsEveryFifteenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(15), AuditProjectionWorker.PollInterval);
}
