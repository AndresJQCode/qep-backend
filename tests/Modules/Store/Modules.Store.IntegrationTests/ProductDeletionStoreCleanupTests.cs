using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Store.Domain;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Lo que el borrado de producto (spec 2026-11-08) le hace a la bandeja de la tienda: una solicitud nueva pierde
/// las líneas de la variante borrada y conserva los totales que vio el comprador; una ya tomada no se toca. Corre
/// el adaptador real de <c>Bootstrapper</c> dentro de la transacción de Catalog, como lo hará el <c>DELETE</c>.
/// Necesitan Docker; se corren una vez en la tanda final.
/// </summary>
public sealed class ProductDeletionStoreCleanupTests
{
    private static readonly object NewCustomerBody = new { newCustomer = new { classificationId = (Guid?)null } };

    private static async Task CleanAsync(StoreInboxRig rig, params Guid[] variantIds)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = rig.Factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>();
        var cleanup = scope.ServiceProvider.GetRequiredService<IProductDeletionCleanup>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await cleanup.ApplyAsync(StoreSettingsFixture.Tenant, variantIds, DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    [Fact]
    public async Task ANewRequestLosesTheLineAndKeepsTheTotalsTheBuyerSaw()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync(lines: [(rig.Variant, 2), (rig.OtherVariant, 3)]);
        var before = await rig.StateAsync(request);

        await CleanAsync(rig, rig.OtherVariant);

        var after = await rig.StateAsync(request);
        Assert.Equal(rig.Variant, Assert.Single(after.Lines).VariantId);
        Assert.Equal(
            (before.Subtotal, before.DiscountAmount, before.TaxAmount, before.Total),
            (after.Subtotal, after.DiscountAmount, after.TaxAmount, after.Total));
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact]
    public async Task AConvertedRequestIsUntouched()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync(lines: [(rig.Variant, 2), (rig.OtherVariant, 3)]);
        await rig.TakeOkAsync(request, NewCustomerBody);
        var before = await rig.StateAsync(request);

        await CleanAsync(rig, rig.OtherVariant);

        var after = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.Converted, after.Status);
        Assert.Equal(
            before.Lines.Select(line => line.VariantId),
            after.Lines.Select(line => line.VariantId));
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task TakingARequestThatLostALineCreatesTheOrderWithTheRestAndShowsTheDifference()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync(lines: [(rig.Variant, 2), (rig.OtherVariant, 3)]);
        await CleanAsync(rig, rig.OtherVariant);

        var taken = await rig.TakeOkAsync(request, NewCustomerBody);

        var order = await rig.OrderAsync((Guid)taken["orderId"]!);
        Assert.Equal(rig.Variant, (Guid)Assert.Single(order["items"]!.AsArray())!["variantId"]!);
        Assert.NotNull((await rig.StateAsync(request)).TotalDifference);
    }

    [Fact]
    public async Task ARequestLeftWithoutLinesCannotBeTakenAndKeepsNoLease()
    {
        await using var rig = await StoreInboxRig.OpenAsync();
        var request = await rig.SubmitAsync(lines: [(rig.OtherVariant, 3)]);
        await CleanAsync(rig, rig.OtherVariant);

        await rig.GetJsonAsync($"/{request}");
        await StoreInboxRig.AssertCodeAsync(
            await rig.TakeAsync(request, NewCustomerBody), HttpStatusCode.UnprocessableEntity, "order.order.items_required");

        var state = await rig.StateAsync(request);
        Assert.Equal(StoreOrderRequestStatus.New, state.Status);
        Assert.Null(state.LeaseMemberId);
    }
}
