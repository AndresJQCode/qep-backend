using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.3: dos conexiones no comparten empresa. Se discrimina por el nombre del índice, no sólo por 23505.</summary>
public sealed class SiigoUnitOfWorkTranslationTests
{
    private static DbUpdateException UniqueViolation(string constraint) =>
        new("save", new PostgresException("duplicate", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: constraint));

    [Fact]
    public void ACompanyTakenByAnotherConnectionIs409()
    {
        var translated = SiigoUnitOfWork.Translate(UniqueViolation(SiigoDbContext.SettingsTenantCompanyIndex));

        Assert.Equal(SiigoErrorCodes.SettingsCompanyTaken, Assert.IsType<ResourceConflictException>(translated).Code);
    }

    [Fact]
    public void AnotherUniqueIndexIsNotTheCompanyOne() =>
        Assert.Null(SiigoUnitOfWork.Translate(UniqueViolation("IX_other")));
}
