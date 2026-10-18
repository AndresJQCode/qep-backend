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

    private static ShopifyOrderMirror CancelledRow()
    {
        var row = ImportedRow(null);
        row.MarkCancelled("""{"reason":"invoiced_before_cancel"}""");
        return row;
    }

    private static ShopifyOrderMirror IgnoredRow()
    {
        var row = NewRow();
        row.MarkIgnored();
        return row;
    }

    // Ruling S13 (§5.4, §6.3): Cancelled e Ignored son terminales.
    [Fact]
    public void CancelledCannotGoBackToWaiting()
    {
        var row = CancelledRow();

        Assert.Throws<InvalidOperationException>(() => row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, null, Updated));
        Assert.Equal((ShopifyImportState.Cancelled, """{"reason":"invoiced_before_cancel"}"""), (row.ImportState, row.WaitDetail));
    }

    [Fact]
    public void CancelledCannotBeIgnored()
    {
        var row = CancelledRow();

        Assert.Throws<InvalidOperationException>(row.MarkIgnored);
        Assert.Equal(ShopifyImportState.Cancelled, row.ImportState);
    }

    [Fact]
    public void IgnoredCannotGoBackToWaiting()
    {
        var row = IgnoredRow();

        Assert.Throws<InvalidOperationException>(() => row.MarkWaiting(ShopifyWaitReasons.StoreNotConfigured, null, Updated));
        Assert.Equal(ShopifyImportState.Ignored, row.ImportState);
    }

    [Fact]
    public void IgnoredCannotBeImported() =>
        Assert.Throws<InvalidOperationException>(() => IgnoredRow().MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, Updated));

    [Fact]
    public void IgnoringTwiceIsIdempotent()
    {
        var row = IgnoredRow();

        row.MarkIgnored();

        Assert.Equal(ShopifyImportState.Ignored, row.ImportState);
    }

    [Fact]
    public void ApplySnapshotOnATerminalRowKeepsTheState()
    {
        var row = CancelledRow();

        Assert.True(row.ApplySnapshot("""{"v":3}""", Updated.AddMinutes(9), Header("refunded")));

        Assert.Equal((ShopifyImportState.Cancelled, """{"v":3}""", "refunded"), (row.ImportState, row.Payload, row.FinancialStatus));
    }

    // Minor 3: la bandeja de «Ignorados» no muestra un motivo de espera que ya no aplica.
    [Fact]
    public void MarkIgnoredClearsTheWait()
    {
        var row = NewRow();
        row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, """{"skus":["SH-400"]}""", Updated);

        row.MarkIgnored();

        Assert.Equal((ShopifyImportState.Ignored, (string?)null, (string?)null), (row.ImportState, row.WaitReason, row.WaitDetail));
    }

    // Important 2: la versión es el token de concurrencia de shopify.orders; cada cambio la sube.
    [Fact]
    public void VersionAdvancesOnEveryChange()
    {
        var row = NewRow();
        Assert.Equal(1L, row.Version);

        row.ApplySnapshot("""{"v":2}""", Updated.AddMinutes(1), Header());
        Assert.Equal(2L, row.Version);
        row.ApplySnapshot("""{"v":0}""", Updated, Header());
        Assert.Equal(2L, row.Version);
        row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, null, Updated);
        Assert.Equal(3L, row.Version);
        row.AttachCustomer(Guid.CreateVersion7());
        Assert.Equal(4L, row.Version);
        row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, """["customer_incomplete"]""", Updated);
        Assert.Equal(5L, row.Version);
        row.ReviewWarnings(Updated);
        Assert.Equal(6L, row.Version);
        row.MarkCancelled(null);
        Assert.Equal(7L, row.Version);

        var ignored = NewRow();
        ignored.MarkIgnored();
        Assert.Equal(2L, ignored.Version);
    }

    // Minor 1: la cabecera que llega de Shopify se acota en el dominio; un valor fuera de los anchos de la columna es un payload
    // inválido que el worker registra y no reintenta, nunca un 22001 de Postgres.
    [Theory]
    [InlineData(65, "pending", "COP")]
    [InlineData(10, "a-financial-status-longer-than-32", "COP")]
    [InlineData(10, "pending", "COPX")]
    [InlineData(10, "pending", "CO")]
    [InlineData(0, "pending", "COP")]
    public void AHeaderOutsideTheColumnWidthsIsRejected(int nameLength, string financialStatus, string currency)
    {
        var header = new ShopifyOrderHeader(new string('#', nameLength), null, Created, null, financialStatus, currency, 1m, false);

        Assert.ThrowsAny<ArgumentException>(() => ShopifyOrderMirror.Create(TenantId, ConnectionId, 5_002L, "{}", Updated, header));
        Assert.ThrowsAny<ArgumentException>(() => NewRow().ApplySnapshot("{}", Updated.AddMinutes(1), header));
    }

    [Fact]
    public void AHeaderAtTheColumnWidthsIsAccepted()
    {
        var header = new ShopifyOrderHeader(new string('#', 64), null, Created, null, new string('p', 32), "usd", 1m, false);

        var row = ShopifyOrderMirror.Create(TenantId, ConnectionId, 5_002L, "{}", Updated, header);

        Assert.Equal((64, 32, "USD"), (row.OrderName.Length, row.FinancialStatus!.Length, row.Currency));
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
    // §6.3: un cambio posterior que Quotations rechazó (order.order.not_pending, payment_has_proofs, otro rechazo de la
    // anulación) queda en last_error y sube la versión, en cualquier estado: la fila importada sigue importada.
    [Fact]
    public void RecordLastErrorStoresTheCodeAndBumpsTheVersion()
    {
        var row = ImportedRow(null);
        var version = row.Version;

        row.RecordLastError(" order.order.not_pending ");

        Assert.Equal(("order.order.not_pending", ShopifyImportState.Imported), (row.LastError, row.ImportState));
        Assert.Equal(version + 1, row.Version);
    }

    // La reacción se repite (bucle de concurrencia de la ingesta, reintento tras un guardado fallido): el mismo error otra vez
    // no es un cambio y no sube la versión.
    [Fact]
    public void RecordLastErrorWithTheSameCodeDoesNothing()
    {
        var row = ImportedRow(null);
        row.RecordLastError("order.order.payment_has_proofs");
        var version = row.Version;

        row.RecordLastError("order.order.payment_has_proofs");

        Assert.Equal(version, row.Version);
    }

    [Fact]
    public void RecordLastErrorCutsAtTheColumnWidth()
    {
        var row = ImportedRow(null);

        row.RecordLastError(new string('x', ShopifyOrderMirror.LastErrorMaxLength + 10));

        Assert.Equal(ShopifyOrderMirror.LastErrorMaxLength, row.LastError!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordLastErrorRequiresAText(string error) =>
        Assert.Throws<ArgumentException>(() => ImportedRow(null).RecordLastError(error));
    // Ronda 1, minor 3: la operación que sale bien después de un rechazo limpia el error viejo; sin error, no hay cambio.
    [Fact]
    public void ClearLastErrorRemovesItAndBumpsTheVersion()
    {
        var row = ImportedRow(null);
        row.RecordLastError("order.order.not_pending");
        var version = row.Version;

        row.ClearLastError();

        Assert.Equal(((string?)null, version + 1), (row.LastError, row.Version));
    }

    [Fact]
    public void ClearLastErrorWithoutErrorDoesNothing()
    {
        var row = ImportedRow(null);
        var version = row.Version;

        row.ClearLastError();

        Assert.Equal(version, row.Version);
    }
}
