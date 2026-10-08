using Microsoft.EntityFrameworkCore;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.UnitTests;

/// <summary>
/// El modelo de EF de Tenancy contra el snapshot, sin abrir una base (mismo criterio que
/// StorageDbContextMappingTests): la migración AddOperatorConsole (spec 2026-10-08 §3) cubre todo el
/// modelo y ningún cambio queda sin migración.
/// </summary>
public sealed class TenancyDbContextMappingTests
{
    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new TenancyDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
