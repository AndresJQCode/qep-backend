using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §5.4 y §6.3: las reglas de la fila del espejo. Un snapshot más viejo no pisa nada (los webhooks llegan
/// desordenados y la conciliación trae lo mismo con retraso), una fila <c>Imported</c> no vuelve a <c>Waiting</c> y «Marcar como
/// revisado» sólo aplica si hay avisos (D-S31).</summary>
public sealed class ShopifyOrderMirrorTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = Created.AddMinutes(10);
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid ConnectionId = Guid.Parse("01900000-0000-7000-8000-0000000000e2");

    private static ShopifyOrderHeader Header(string? financialStatus = "pending", decimal total = 119_000m) =>
        new("#1001", 7_001L, Created, null, financialStatus, "COP", total, false);

    private static ShopifyOrderMirror NewRow() =>
        ShopifyOrderMirror.Create(TenantId, ConnectionId, 5_001L, """{"v":1}""", Updated, Header());

    private static ShopifyOrderMirror ImportedRow(string? warningsJson)
    {
        var row = NewRow();
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, warningsJson, Updated.AddMinutes(1));
        return row;
    }

    [Fact]
    public void CreateStartsWaitingWithoutReason()
    {
        var row = NewRow();

        Assert.Equal((ShopifyImportState.Waiting, (string?)null, 0), (row.ImportState, row.WaitReason, row.Attempts));
        Assert.Equal(("#1001", 5_001L, (long?)7_001L, "COP", 119_000m), (row.OrderName, row.ShopifyOrderId, row.ShopifyCustomerId, row.Currency, row.TotalPrice));
        Assert.Equal((Updated, """{"v":1}"""), (row.ShopifyUpdatedAt, row.Payload));
    }

    [Fact]
    public void ApplySnapshotOlderDoesNothing()
    {
        var row = NewRow();

        var applied = row.ApplySnapshot("""{"v":0}""", Updated.AddMinutes(-1), Header("paid", 1m));

        Assert.False(applied);
        Assert.Equal(("""{"v":1}""", Updated, "pending", 119_000m), (row.Payload, row.ShopifyUpdatedAt, row.FinancialStatus, row.TotalPrice));
    }

    [Fact]
    public void ApplySnapshotNewerReplacesPayloadAndHeader()
    {
        var row = NewRow();

        var applied = row.ApplySnapshot("""{"v":2}""", Updated.AddMinutes(5), Header("paid", 120_000m));

        Assert.True(applied);
        Assert.Equal(("""{"v":2}""", Updated.AddMinutes(5), "paid", 120_000m), (row.Payload, row.ShopifyUpdatedAt, row.FinancialStatus, row.TotalPrice));
    }

    [Fact]
    public void MarkWaitingRecordsReasonDetailAndAttempt()
    {
        var row = NewRow();
        var attemptedAt = Updated.AddMinutes(1);

        row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, """{"skus":["SH-400"]}""", attemptedAt);

        Assert.Equal((ShopifyImportState.Waiting, "product_unmatched", """{"skus":["SH-400"]}"""), (row.ImportState, row.WaitReason, row.WaitDetail));
        Assert.Equal((1, (DateTimeOffset?)attemptedAt), (row.Attempts, row.LastAttemptAt));
    }

    [Fact]
    public void MarkWaitingRejectsAReasonOutsideTheList() =>
        Assert.Throws<ArgumentException>(() => NewRow().MarkWaiting("freight_unsupported", null, Updated));

    [Fact]
    public void MarkImportedClearsTheWaitAndKeepsTheReferences()
    {
        var row = NewRow();
        row.MarkWaiting(ShopifyWaitReasons.StoreNotConfigured, null, Updated);
        var customerId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        row.MarkImported(customerId, orderId, 500m, """["total_difference"]""", Updated.AddMinutes(2));

        Assert.Equal((ShopifyImportState.Imported, (string?)null, (string?)null), (row.ImportState, row.WaitReason, row.WaitDetail));
        Assert.Equal(((Guid?)customerId, (Guid?)orderId, (decimal?)500m), (row.QepCustomerId, row.QepOrderId, row.TotalDifference));
        Assert.Equal(("""["total_difference"]""", (DateTimeOffset?)Updated.AddMinutes(2), 2), (row.Warnings, row.ImportedAt, row.Attempts));
    }

    [Fact]
    public void ImportedCannotGoBackToWaiting()
    {
        var row = ImportedRow(null);

        Assert.Throws<InvalidOperationException>(() => row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, null, Updated));
        Assert.Equal(ShopifyImportState.Imported, row.ImportState);
    }

    [Fact]
    public void ImportedCannotBeIgnored() =>
        Assert.Throws<InvalidOperationException>(() => ImportedRow(null).MarkIgnored());

    [Fact]
    public void WaitingCancelledInShopifyIsIgnored()
    {
        var row = NewRow();

        row.MarkIgnored();

        Assert.Equal(ShopifyImportState.Ignored, row.ImportState);
    }

    [Fact]
    public void ImportedCanBeCancelledWithDetail()
    {
        var row = ImportedRow(null);

        row.MarkCancelled("""{"reason":"invoiced_before_cancel"}""");

        Assert.Equal((ShopifyImportState.Cancelled, """{"reason":"invoiced_before_cancel"}"""), (row.ImportState, row.WaitDetail));
    }

    [Fact]
    public void WaitingCannotBeCancelled() =>
        Assert.Throws<InvalidOperationException>(() => NewRow().MarkCancelled(null));

    [Fact]
    public void AttachCustomerKeepsTheIdForTheNextAttempt()
    {
        var row = NewRow();
        var customerId = Guid.CreateVersion7();

        row.AttachCustomer(customerId);

        Assert.Equal((Guid?)customerId, row.QepCustomerId);
        Assert.Throws<ArgumentException>(() => row.AttachCustomer(Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public void ReviewWarningsWithoutWarningsThrows(string? warningsJson)
    {
        var row = ImportedRow(warningsJson);

        var error = Assert.Throws<ShopifyDomainException>(() => row.ReviewWarnings(Updated));

        Assert.Equal("shopify.order.no_warnings", error.Code);
        Assert.Null(row.WarningsReviewedAt);
    }

    [Fact]
    public void ReviewWarningsStampsTheReview()
    {
        var row = ImportedRow("""["customer_incomplete"]""");
        var reviewedAt = Updated.AddHours(1);

        row.ReviewWarnings(reviewedAt);

        Assert.Equal((DateTimeOffset?)reviewedAt, row.WarningsReviewedAt);
    }
}
