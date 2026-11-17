using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Messaging.UnitTests;

/// <summary>Revisión de la rama, hallazgo 3: una segunda delegación viva de la conversación choca con <c>IX_bot_delegations_live</c>. Se
/// traduce en Infrastructure, por nombre de índice y no sólo por 23505, a la excepción de dominio que el motor de chatbots ya sabe atender
/// (cierra la corrida <c>Interrupted</c> en vez de reintentar la fila hasta rendirse).</summary>
public sealed class LiveDelegationConflictTests
{
    private static DbUpdateException Violation(string sqlState, string? constraint) =>
        new("save failed", new PostgresException("dup", "ERROR", "ERROR", sqlState, constraintName: constraint));

    [Fact]
    public void TheLiveDelegationIndexIsAConflict() =>
        Assert.True(MessagingSaveChanges.IsLiveDelegationConflict(Violation(PostgresErrorCodes.UniqueViolation, "IX_bot_delegations_live")));

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "IX_automation_inbox_delegation")]
    [InlineData(PostgresErrorCodes.UniqueViolation, null)]
    [InlineData(PostgresErrorCodes.CheckViolation, "IX_bot_delegations_live")]
    public void AnyOtherViolationIsNot(string sqlState, string? constraint) =>
        Assert.False(MessagingSaveChanges.IsLiveDelegationConflict(Violation(sqlState, constraint)));

    [Fact]
    public void TheIndexNameIsTheOneOfTheModel() =>
        Assert.Equal("IX_bot_delegations_live", MessagingDbContext.BotDelegationLiveIndex);
}
