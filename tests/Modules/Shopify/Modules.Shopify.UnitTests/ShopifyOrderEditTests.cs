using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-14 §4: las transiciones de un cambio de Shopify. Sólo un cambio abierto (pendiente o bloqueado) se
/// decide, y «revisado» es sólo para uno bloqueado (plan D9).</summary>
public sealed class ShopifyOrderEditTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

    private static ShopifyOrderEdit NewEdit(ShopifyOrderEditStatus status = ShopifyOrderEditStatus.Pending, string? reason = null) =>
        ShopifyOrderEdit.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now.AddHours(-1), Now,
            """{"name":"#1001"}""", "[]", status, reason, Now);

    [Fact]
    public void ReviewOnlyFromBlocked()
    {
        var edit = NewEdit();

        var error = Assert.Throws<ShopifyDomainException>(() => edit.Review(Guid.CreateVersion7(), Now));

        Assert.Equal("shopify.edit.not_blocked", error.Code);
        Assert.Equal(ShopifyOrderEditStatus.Pending, edit.Status);

        var blocked = NewEdit(ShopifyOrderEditStatus.Blocked, ShopifyEditReasons.OrderNotEditable);
        var by = Guid.CreateVersion7();
        blocked.Review(by, Now.AddMinutes(1));
        Assert.Equal((ShopifyOrderEditStatus.Reviewed, (Guid?)by, (DateTimeOffset?)Now.AddMinutes(1)), (blocked.Status, blocked.DecidedBy, blocked.DecidedAt));
    }

    [Fact]
    public void AClosedEditCannotBeDecided()
    {
        var edit = NewEdit();
        edit.MarkApplied("[]", null, Now);

        var error = Assert.Throws<ShopifyDomainException>(() => edit.Reject(Guid.CreateVersion7(), Now));

        Assert.Equal("shopify.edit.not_open", error.Code);
        Assert.Equal(ShopifyOrderEditStatus.Applied, edit.Status);
    }

    [Fact]
    public void SupersedeClosesAnOpenEdit()
    {
        var edit = NewEdit(ShopifyOrderEditStatus.Blocked, ShopifyEditReasons.PaymentStatusNotAllowed);
        var version = edit.Version;

        edit.Supersede(Now.AddMinutes(5));

        Assert.False(edit.IsOpen);
        Assert.Equal((ShopifyOrderEditStatus.Superseded, (Guid?)null, version + 1), (edit.Status, edit.DecidedBy, edit.Version));
    }

    [Fact]
    public void BlockKeepsTheReasonAndApplyClearsIt()
    {
        var edit = NewEdit();

        edit.Block(ShopifyEditReasons.OrderNotEditable, """[{"kind":"Note"}]""", Now);
        Assert.Equal((ShopifyOrderEditStatus.Blocked, "order_not_editable"), (edit.Status, edit.StatusReason));

        var by = Guid.CreateVersion7();
        edit.MarkApplied("[]", by, Now.AddMinutes(1));
        Assert.Equal((ShopifyOrderEditStatus.Applied, (string?)null, (Guid?)by, "[]"), (edit.Status, edit.StatusReason, edit.DecidedBy, edit.Groups));
    }
}
