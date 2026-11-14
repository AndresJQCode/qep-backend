using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §5: un envío a Shopify por pedido mientras está pendiente; encolar otro grupo lo suma al mismo.</summary>
public sealed class ShopifyOrderPushTests
{
    private static ShopifyOrderMirror ImportedRow()
    {
        var world = new ShopifyEditTestWorld();
        return world.ImportedRow();
    }

    [Fact]
    public void MergeUnitesTheGroups()
    {
        var push = ShopifyOrderPush.Create(ImportedRow(), [ShopifyPushGroup.Note], ImporterWorld.Now);

        push.Merge([ShopifyPushGroup.ShippingAddress, ShopifyPushGroup.Note], ImporterWorld.Now.AddMinutes(1));

        Assert.Equal([ShopifyPushGroup.ShippingAddress, ShopifyPushGroup.Note], push.Groups);
        Assert.Equal((ShopifyOrderPushStatus.Pending, 0, 2L), (push.Status, push.Attempts, push.Version));
        Assert.Equal(ImporterWorld.Now.AddMinutes(1), push.UpdatedAt);
    }

    // Review Important 1: una corrección del mismo grupo mientras el worker manda tiene que invalidar su MarkSent (la versión es el
    // token de concurrencia); si no, Shopify se queda con el valor viejo y el envío dice Sent.
    [Fact]
    public void MergeWithTheSameGroupsStillBumpsTheVersion()
    {
        var push = ShopifyOrderPush.Create(ImportedRow(), [ShopifyPushGroup.Note], ImporterWorld.Now);

        push.Merge([ShopifyPushGroup.Note], ImporterWorld.Now.AddMinutes(1));

        Assert.Equal([ShopifyPushGroup.Note], push.Groups);
        Assert.Equal((2L, ImporterWorld.Now.AddMinutes(1)), (push.Version, push.UpdatedAt));
    }

    [Fact]
    public void ASentPushDoesNotMerge()
    {
        var push = ShopifyOrderPush.Create(ImportedRow(), [ShopifyPushGroup.Note], ImporterWorld.Now);
        push.MarkSent(ImporterWorld.Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => push.Merge([ShopifyPushGroup.Attributes], ImporterWorld.Now.AddMinutes(2)));
        Assert.Equal((ShopifyOrderPushStatus.Sent, (DateTimeOffset?)ImporterWorld.Now.AddMinutes(1)), (push.Status, push.SentAt));
    }

    [Fact]
    public void MarkFailedKeepsTheCodeWithinItsColumn()
    {
        var push = ShopifyOrderPush.Create(ImportedRow(), [ShopifyPushGroup.Note], ImporterWorld.Now);

        push.MarkFailed(new string('x', 80), ImporterWorld.Now.AddMinutes(1));

        Assert.Equal(ShopifyOrderPushStatus.Failed, push.Status);
        Assert.Equal(ShopifyOrderPush.FailureCodeMaxLength, push.FailureCode!.Length);
    }

    [Fact]
    public void ARowWithoutAnOrderCannotPush()
    {
        var waiting = ShopifyOrderMirror.Create(
            ImporterWorld.TenantId, ImporterWorld.ConnectionId, 1L, "{}", ImporterWorld.Now, ImporterWorld.Order().ToHeader());

        Assert.Throws<InvalidOperationException>(() => ShopifyOrderPush.Create(waiting, [ShopifyPushGroup.Note], ImporterWorld.Now));
    }
}
