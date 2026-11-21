using Microsoft.EntityFrameworkCore;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// El modelo de EF de Siigo contra el snapshot, sin abrir una base (mismo criterio que StorageDbContextMappingTests). La columna
/// <c>siigo.settings.seller_id</c> se borró en un release posterior al que dejó de usarla (spec 2026-11-20): ya no hay imagen en
/// ejecución que la lea, así que no queda mapeada.
/// </summary>
public sealed class SiigoDbContextMappingTests
{
    private static readonly string[] CallFailureColumns =
    [
        "id", "tenant_id", "connection_id", "operation", "method", "path", "status", "reason", "idempotency_key", "siigo_invoice_id",
        "request_curl", "response_body", "subject_id", "trace_id", "occurred_at",
    ];

    [Fact]
    public void TheModelHasNoChangesPendingAMigration()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheSellerColumnIsNoLongerMapped()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        Assert.Null(context.Model.FindEntityType(typeof(SiigoSettings))!.FindProperty("SellerId"));
    }

    [Fact]
    public void TheCallFailuresMapToTheirTableWithTheirColumnsAndIndexes()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var failure = context.Model.FindEntityType(typeof(SiigoCallFailure))!;

        Assert.Equal(("call_failures", "siigo"), (failure.GetTableName(), failure.GetSchema()));
        Assert.Equal("PK_call_failures", failure.FindPrimaryKey()!.GetName());
        Assert.Equal(
            CallFailureColumns.Order(StringComparer.Ordinal),
            failure.GetProperties().Select(property => property.GetColumnName()).Order(StringComparer.Ordinal));
        Assert.Equal(40, failure.FindProperty(nameof(SiigoCallFailure.Operation))!.GetMaxLength());
        Assert.Equal("text", failure.FindProperty(nameof(SiigoCallFailure.RequestCurl))!.GetColumnType());
        Assert.Equal("text", failure.FindProperty(nameof(SiigoCallFailure.ResponseBody))!.GetColumnType());
        Assert.Contains(failure.GetIndexes(), index => index.GetDatabaseName() == "IX_call_failures_tenant_occurred");
        Assert.Contains(failure.GetIndexes(), index => index.GetDatabaseName() == "IX_call_failures_occurred");
    }

    [Fact]
    public void TheSellerLinksMapToTheirTable()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var link = context.Model.FindEntityType(typeof(SiigoSellerLink))!;

        Assert.Equal(("seller_links", "siigo"), (link.GetTableName(), link.GetSchema()));
        Assert.Equal(["ConnectionId", "MembershipId"], link.FindPrimaryKey()!.Properties.Select(property => property.Name));
    }
}
