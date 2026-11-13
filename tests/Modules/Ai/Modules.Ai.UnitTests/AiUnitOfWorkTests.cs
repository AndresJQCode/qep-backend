using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Ai.UnitTests;

/// <summary>Plan 2026-11-12 (correcciones de la revisión), Tarea 1: dos guardados que igual chocan al sincronizar la ocupación responden
/// 412 y no 500. La carrera real no se reproduce de forma determinista; acá se amarra qué cuenta como carrera.</summary>
public sealed class AiUnitOfWorkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public void ADeadlockOrSerializationFailureIsAWriteRace(string sqlState) =>
        Assert.True(AiUnitOfWork.IsWriteRace(
            new InvalidOperationException("wrapper", new PostgresException("race", "ERROR", "ERROR", sqlState))));

    [Fact]
    public void AUniqueViolationIsNotAWriteRace() =>
        Assert.False(AiUnitOfWork.IsWriteRace(new PostgresException("dup", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));

    /// <summary>Plan 2026-11-12, Task 6: the catch itself, not only the predicate. Wrapped is what EF's SaveChanges throws; bare is what
    /// the slot sync's <c>ExecuteSqlAsync</c> (or the commit) throws.</summary>
    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected, true)]
    [InlineData(PostgresErrorCodes.SerializationFailure, true)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, false)]
    [InlineData(PostgresErrorCodes.SerializationFailure, false)]
    public async Task ARaceWhileSavingIsAConcurrencyConflict(string sqlState, bool wrapped)
    {
        var race = new PostgresException("race", "ERROR", "ERROR", sqlState);
        Exception thrown = wrapped ? new DbUpdateException("save failed", race) : race;
        await using var context = ContextThatThrowsOnSave(thrown);

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => new AiUnitOfWork(context, new NoSlots()).SaveChangesAsync(Ct));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Same(thrown, error.InnerException);
    }

    [Fact]
    public async Task AnotherDatabaseErrorIsNotMappedToAConflict()
    {
        var thrown = new DbUpdateException(
            "save failed",
            new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation));
        await using var context = ContextThatThrowsOnSave(thrown);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => new AiUnitOfWork(context, new NoSlots()).SaveChangesAsync(Ct));

        Assert.Same(thrown, error);
    }

    /// <summary>No connection is opened: with nothing tracked the unit opens no transaction, and the interceptor throws before EF
    /// touches the database.</summary>
    private static AiDbContext ContextThatThrowsOnSave(Exception error) =>
        new(new DbContextOptionsBuilder<AiDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused")
            .AddInterceptors(new ThrowOnSave(error))
            .Options);

    private sealed class ThrowOnSave(Exception error) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(error);
    }

    private sealed class NoSlots : IAgentConnectionSlots
    {
        public Task<IReadOnlyList<AiConnectionOccupant>> SyncAgentsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionOccupant>>([]);

        public Task<IReadOnlyList<AiConnectionOccupant>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionOccupant>>([]);
    }
}
