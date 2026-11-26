using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Plan 2026-11-17, Tarea 5: el trabajo de importación de productos y sus contadores (D3: productos y variantes).</summary>
public sealed class ShopifyProductImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    private static ShopifyProductImport NewImport() =>
        ShopifyProductImport.Request(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ShopifyProductImportMode.All, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    [Fact]
    public void ARequestedImportIsPendingWithZeroCounters()
    {
        var import = NewImport();

        Assert.Equal(ShopifyProductImportStatus.Pending, import.Status);
        Assert.Equal(
            (0, 0, 0, 0, 0, 0, 0),
            (import.Total, import.Done, import.Created, import.Matched, import.WithoutSku, import.Skipped, import.Attempts));
        Assert.Null(import.Cursor);
        Assert.Null(import.FailureCode);
        Assert.Null(import.FinishedAt);
        Assert.Equal(Now, import.CreatedAt);
    }

    [Fact]
    public void RecordPageAddsTheTallyAndKeepsTheCursor()
    {
        var import = NewImport();
        import.Start(30);

        import.RecordPage(new ShopifyProductTally(10, 4, 6, 2, 1), "cursor-1");
        import.RecordPage(new ShopifyProductTally(5, 1, 3, 0, 2), "cursor-2");

        Assert.Equal(ShopifyProductImportStatus.Running, import.Status);
        Assert.Equal((30, 15, 5, 9, 2, 3), (import.Total, import.Done, import.Created, import.Matched, import.WithoutSku, import.Skipped));
        Assert.Equal("cursor-2", import.Cursor);
    }

    [Fact]
    public void FailKeepsTheCountersAndSetsTheCode()
    {
        var import = NewImport();
        import.Start(10);
        import.RecordPage(new ShopifyProductTally(3, 3, 0, 0, 0), "c");

        import.Fail(ShopifyProductCodes.Unreachable, Now.AddMinutes(2));

        Assert.Equal(ShopifyProductImportStatus.Failed, import.Status);
        Assert.Equal("unreachable", import.FailureCode);
        Assert.Equal(Now.AddMinutes(2), import.FinishedAt);
        Assert.Equal((3, 3), (import.Done, import.Created));
    }

    [Fact]
    public void CompleteFinishesTheImport()
    {
        var import = NewImport();
        import.Start(0);

        import.Complete(Now.AddMinutes(1));

        Assert.Equal((ShopifyProductImportStatus.Done, (DateTimeOffset?)Now.AddMinutes(1)), (import.Status, import.FinishedAt));
    }

    [Fact]
    public void AnIssueTitleIsFittedTo255()
    {
        var issue = ShopifyProductImportIssue.Create(Guid.CreateVersion7(), 42L, new string('x', 300), "partial_match", Now);

        Assert.Equal(ShopifyProductImportIssue.TitleMaxLength, issue.Title.Length);
        Assert.Equal("partial_match", issue.Code);
    }

    [Fact]
    public void MarkProductsSyncedDoesNotBumpTheStoreVersion()
    {
        var store = ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        store.MarkProductsSynced(Now.AddHours(1));

        Assert.Equal((Now.AddHours(1), 1L), (store.ProductsSyncedAt!.Value, store.Version));
    }
}
