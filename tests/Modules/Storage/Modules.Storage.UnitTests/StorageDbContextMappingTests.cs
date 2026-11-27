using Microsoft.EntityFrameworkCore;
using Modules.Storage.Infrastructure.Persistence;

namespace Modules.Storage.UnitTests;

/// <summary>
/// El modelo de EF de Storage contra el snapshot, sin abrir una base (mismo criterio que
/// QuotationsDbContextMappingTests): el inbox nuevo (spec 2026-09-16, D9) y que ningún cambio de
/// modelo quede sin migración.
/// </summary>
public sealed class StorageDbContextMappingTests
{
    [Fact]
    public void TheInboxMapsToItsTableWithConsumerAndMessageAsKey()
    {
        using var context = new StorageDbContextFactory().CreateDbContext([]);

        var inbox = context.Model.FindEntityType(typeof(StorageInboxMessage));

        Assert.NotNull(inbox);
        Assert.Equal("inbox_messages", inbox.GetTableName());
        Assert.Equal("storage", inbox.GetSchema());
        var key = inbox.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Equal(["Consumer", "MessageId"], key.Properties.Select(property => property.Name));

        // Spec 2026-11-26 («Consumidor»): una fila reclamada y sin terminar lleva processed_at en null, hasta cuándo nadie la vuelve a
        // tomar (claimed_until) y cuántos reclamos lleva (attempts). StorageInboxClaims escribe estas columnas en SQL crudo.
        var processedAt = inbox.FindProperty("ProcessedAt");
        Assert.NotNull(processedAt);
        Assert.Equal("processed_at", processedAt.GetColumnName());
        Assert.True(processedAt.IsNullable);
        var claimedUntil = inbox.FindProperty("ClaimedUntil");
        Assert.NotNull(claimedUntil);
        Assert.Equal("claimed_until", claimedUntil.GetColumnName());
        Assert.True(claimedUntil.IsNullable);
        var attempts = inbox.FindProperty("Attempts");
        Assert.NotNull(attempts);
        Assert.Equal("attempts", attempts.GetColumnName());
        Assert.False(attempts.IsNullable);
        Assert.Equal(1, attempts.GetDefaultValue());
    }

    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new StorageDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
