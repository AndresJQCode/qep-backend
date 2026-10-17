using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-17 §5.4: el historial del pedido cuenta el cambio de envío con los mismos textos que
/// la cotización.</summary>
public sealed class OrderChangeSummaryTests
{
    [Fact]
    public void DescribesTheShippingChange()
    {
        var with = new ShippingDetails(
            Guid.CreateVersion7(), "Coordinadora", FreightChargeMode.Charged, 15000m, 19, CashOnDelivery: true);

        Assert.Equal(
            "Editó transportadora (Coordinadora), cobro del envío (cobrado), valor del envío (15000), contraentrega.",
            OrderChangeSummary.ShippingChanged(ShippingDetails.None, with));
        Assert.Equal(
            "Editó quitó la transportadora, quitó el cobro del envío, quitó la contraentrega.",
            OrderChangeSummary.ShippingChanged(with, ShippingDetails.None));
        Assert.Equal(
            "Editó valor del envío (18000).",
            OrderChangeSummary.ShippingChanged(with, with with { FreightAmount = 18000m }));
    }

    [Fact]
    public void AnUnchangedShippingHasNoSummary() =>
        Assert.Null(OrderChangeSummary.ShippingChanged(ShippingDetails.None, ShippingDetails.None));
}
