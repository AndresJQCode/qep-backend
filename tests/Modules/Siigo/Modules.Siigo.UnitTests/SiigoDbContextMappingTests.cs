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
    public void ThePriceModeIsTextNotNullWithTheBaseWithDiscountDefault()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var property = context.Model.FindEntityType(typeof(SiigoSettings))!.FindProperty(nameof(SiigoSettings.PriceMode))!;

        Assert.Equal("price_mode", property.GetColumnName());
        Assert.False(property.IsNullable);
        Assert.Equal(typeof(string), property.GetProviderClrType());
        Assert.Equal(SiigoPriceMode.BaseWithDiscount, property.GetDefaultValue());
    }

    [Fact]
    public void TheBackgroundInvoicingColumnsAndTheOpenOrderIndexAreMapped()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var invoice = context.Model.FindEntityType(typeof(SiigoInvoice))!;
        var item = context.Model.FindEntityType(typeof(SiigoInvoiceBatchItem))!;

        var unanswered = invoice.FindProperty(nameof(SiigoInvoice.UnansweredAt))!;
        var lastUnanswered = invoice.FindProperty(nameof(SiigoInvoice.LastUnansweredAt))!;
        Assert.Equal(("unanswered_at", true), (unanswered.GetColumnName(), unanswered.IsNullable));
        Assert.Equal(("last_unanswered_at", true), (lastUnanswered.GetColumnName(), lastUnanswered.IsNullable));

        var invoiceId = item.FindProperty(nameof(SiigoInvoiceBatchItem.InvoiceId))!;
        var payments = item.FindProperty(nameof(SiigoInvoiceBatchItem.PaymentsJson))!;
        var markFailed = item.FindProperty(nameof(SiigoInvoiceBatchItem.OrderMarkFailed))!;
        Assert.Equal(("invoice_id", true), (invoiceId.GetColumnName(), invoiceId.IsNullable));
        Assert.DoesNotContain(item.GetForeignKeys(), key => key.Properties.Contains(invoiceId));
        Assert.Equal(("payments_json", true, "text"), (payments.GetColumnName(), payments.IsNullable, payments.GetColumnType()));
        Assert.Equal(("order_mark_failed", false), (markFailed.GetColumnName(), markFailed.IsNullable));
        Assert.Equal(false, markFailed.GetDefaultValue());

        var index = Assert.Single(
            item.GetIndexes(), value => value.GetDatabaseName() == "IX_invoice_batch_items_tenant_order_open");
        Assert.True(index.IsUnique);
        Assert.Equal(["TenantId", "OrderId"], index.Properties.Select(property => property.Name));
        Assert.Equal("status IN ('Pending', 'Sending')", index.GetFilter());
    }

    // Spec 2026-11-25 §1: una anulada en Siigo no es vigente, así que no ocupa el lugar de la factura nueva del pedido.
    [Fact]
    public void TheCurrentInvoiceIndexLeavesOutAnnulledRows()
    {
        using var context = new SiigoDbContextFactory().CreateDbContext([]);

        var invoice = context.Model.FindEntityType(typeof(SiigoInvoice))!;

        var index = Assert.Single(
            invoice.GetIndexes(), value => value.GetDatabaseName() == SiigoDbContext.InvoicesTenantOrderKindIndex);
        Assert.True(index.IsUnique);
        Assert.Equal("status NOT IN ('Rejected', 'Failed', 'Annulled')", index.GetFilter());
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
