using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Spec 2026-11-23: la conciliación de los regalos de «Editar pedido» (OrderGiftEdits).
public sealed class OrderGiftEditsTests
{
    [Fact]
    public void GiftQuantityChangedNamesTheGift() =>
        Assert.Equal(
            "Cambió la cantidad del regalo Aceite: 1 → 3.",
            OrderChangeSummary.GiftQuantityChanged("Aceite", 1m, 3m, HistoryFormat.Colombian));
}
