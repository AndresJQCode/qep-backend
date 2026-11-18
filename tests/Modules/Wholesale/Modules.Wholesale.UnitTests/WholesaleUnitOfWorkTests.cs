using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Wholesale.Infrastructure.Persistence;
using Npgsql;

namespace Modules.Wholesale.UnitTests;

/// <summary>Cada violación de la base sale con su código del contrato, discriminada por nombre (no sólo por SqlState).</summary>
public sealed class WholesaleUnitOfWorkTests
{
    private static DbUpdateException Violation(string sqlState, string constraint) =>
        new("save", new PostgresException("violation", "ERROR", "ERROR", sqlState, constraintName: constraint));

    [Theory]
    [InlineData("IX_price_lists_tenant_name", "wholesale.price_list.name_taken")]
    [InlineData("IX_distributors_tenant_identification", "wholesale.distributor.identification_taken")]
    [InlineData("IX_distributors_tenant_distributor_tenant", "wholesale.distributor.already_linked")]
    public void UniqueIndexesAre409ByName(string index, string code)
    {
        var translated = Assert.IsType<ResourceConflictException>(
            WholesaleUnitOfWork.Translate(Violation(PostgresErrorCodes.UniqueViolation, index)));

        Assert.Equal(code, translated.Code);
    }

    [Fact]
    public void ThePriceListForeignKeyIs409InUse()
    {
        var translated = Assert.IsType<ResourceConflictException>(WholesaleUnitOfWork.Translate(
            Violation(PostgresErrorCodes.ForeignKeyViolation, "FK_distributors_price_lists_price_list_id")));

        Assert.Equal("wholesale.price_list.in_use", translated.Code);
    }

    [Fact]
    public void AConcurrencyConflictIs412()
    {
        var translated = Assert.IsType<RequestConcurrencyException>(
            WholesaleUnitOfWork.Translate(new DbUpdateConcurrencyException("stale")));

        Assert.Equal("concurrency.conflict", translated.Code);
    }

    [Fact]
    public void AnotherConstraintIsNotTranslated()
    {
        Assert.Null(WholesaleUnitOfWork.Translate(Violation(PostgresErrorCodes.UniqueViolation, "IX_distributor_invitations_token_hash")));
        Assert.Null(WholesaleUnitOfWork.Translate(Violation(PostgresErrorCodes.ForeignKeyViolation, "IX_price_lists_tenant_name")));
    }
}
