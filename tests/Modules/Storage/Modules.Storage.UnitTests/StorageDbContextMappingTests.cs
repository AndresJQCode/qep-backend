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
    }

    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new StorageDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
