using Modules.Automations.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Automations.UnitTests;

/// <summary>Plan 2026-11-12 (correcciones de la revisión), Tarea 1: dos guardados que igual chocan al sincronizar la ocupación responden
/// 412 y no 500. La carrera real no se reproduce de forma determinista; acá se amarra qué cuenta como carrera.</summary>
public sealed class AutomationsUnitOfWorkTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public void ADeadlockOrSerializationFailureIsAWriteRace(string sqlState) =>
        Assert.True(AutomationsUnitOfWork.IsWriteRace(
            new InvalidOperationException("wrapper", new PostgresException("race", "ERROR", "ERROR", sqlState))));

    [Fact]
    public void AUniqueViolationIsNotAWriteRace() =>
        Assert.False(AutomationsUnitOfWork.IsWriteRace(new PostgresException("dup", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));
}
