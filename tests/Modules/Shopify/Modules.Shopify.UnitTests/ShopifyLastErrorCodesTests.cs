using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Decisión del owner del 2026-10-20: <c>last_error</c> es texto libre (soporte, logs) y la bandeja pinta
/// <c>lastErrorCodes</c>, que sale de esta normalización. Los casos son los valores que escriben de verdad los cuatro orígenes
/// de <c>last_error</c> (rechazos de Quotations unidos por <c>"; "</c>, el nombre del enum cuando no hay código, el
/// <c>invalid_payload: …</c> del parser y el motivo de un salto de la conciliación).
/// </summary>
public sealed class ShopifyLastErrorCodesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoErrorIsAnEmptyList(string? lastError) =>
        Assert.Empty(ShopifyLastErrorCodes.Normalize(lastError));

    [Fact]
    public void AQuotationsCodeTravelsAsIs() =>
        Assert.Equal(["order.order.not_pending"], ShopifyLastErrorCodes.Normalize("order.order.not_pending"));

    [Fact]
    public void JoinedCodesAreSplitInOrder() =>
        Assert.Equal(
            ["order.order.not_found", "order.order.payment_has_proofs"],
            ShopifyLastErrorCodes.Normalize("order.order.not_found; order.order.payment_has_proofs"));

    [Theory]
    [InlineData("invalid_payload: updatedAt")]
    [InlineData("invalid_payload: id: The Shopify order payload has no 'id'.")]
    [InlineData("invalid_payload: payload: The mirror payload is not a readable snapshot.")]
    [InlineData("invalid_payload")]
    public void AnInvalidPayloadIsOneCode(string lastError) =>
        Assert.Equal([ShopifyLastErrorCodes.InvalidPayload], ShopifyLastErrorCodes.Normalize(lastError));

    [Theory]
    [InlineData("Ignored")]
    [InlineData("Unchanged")]
    [InlineData("AlreadyCancelled")]
    [InlineData("The Shopify order could not be read.")]
    [InlineData("order")]
    [InlineData("Order.Order.Not_Pending")]
    public void AnythingElseIsSyncFailed(string lastError) =>
        Assert.Equal([ShopifyLastErrorCodes.SyncFailed], ShopifyLastErrorCodes.Normalize(lastError));

    [Fact]
    public void DuplicatesCollapseKeepingTheFirstPosition() =>
        Assert.Equal(
            [ShopifyLastErrorCodes.SyncFailed, "order.order.not_pending"],
            ShopifyLastErrorCodes.Normalize("Ignored; order.order.not_pending; Unchanged; order.order.not_pending"));

    [Fact]
    public void PartsAreTrimmedAndEmptyPartsSkipped() =>
        Assert.Equal(
            ["order.order.not_pending", ShopifyLastErrorCodes.InvalidPayload],
            ShopifyLastErrorCodes.Normalize("  order.order.not_pending ;  ; invalid_payload: id  "));

    [Fact]
    public void TheCodesAreTheContractedOnes() =>
        Assert.Equal(
            ("shopify.order.invalid_payload", "shopify.order.sync_failed"),
            (ShopifyLastErrorCodes.InvalidPayload, ShopifyLastErrorCodes.SyncFailed));
}
