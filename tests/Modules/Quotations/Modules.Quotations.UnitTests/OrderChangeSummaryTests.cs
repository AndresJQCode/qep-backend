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

    // El historial dice a quién se le mandó: quien factura no siempre es quien recibe.
    [Theory]
    [InlineData(QuotationRecipient.Customer, "Enviado por WhatsApp al cliente.")]
    [InlineData(QuotationRecipient.Billing, "Enviado por WhatsApp a facturación.")]
    public void DescribesTheSendWithItsRecipient(QuotationRecipient recipient, string expected) =>
        Assert.Equal(expected, OrderChangeSummary.Sent(recipient));

    [Fact]
    public void AnUnchangedShippingHasNoSummary() =>
        Assert.Null(OrderChangeSummary.ShippingChanged(ShippingDetails.None, ShippingDetails.None));
}
