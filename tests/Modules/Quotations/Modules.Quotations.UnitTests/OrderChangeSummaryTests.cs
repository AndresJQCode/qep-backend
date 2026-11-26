using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-17 §5.4: el historial del pedido cuenta el cambio de envío con los mismos textos que
/// la cotización.</summary>
public sealed class OrderChangeSummaryTests
{
    private static readonly HistoryFormat Colombian = HistoryFormat.Colombian;

    [Fact]
    public void DescribesTheShippingChange()
    {
        var with = new ShippingDetails(
            Guid.CreateVersion7(), "Coordinadora", FreightChargeMode.Charged, 15000m, 19, CashOnDelivery: true);

        Assert.Equal(
            "Editó transportadora (Coordinadora), cobro del envío (cobrado), valor del envío (15.000), contraentrega.",
            OrderChangeSummary.ShippingChanged(ShippingDetails.None, with, Colombian));
        Assert.Equal(
            "Editó quitó la transportadora, quitó el cobro del envío, quitó la contraentrega.",
            OrderChangeSummary.ShippingChanged(with, ShippingDetails.None, Colombian));
        Assert.Equal(
            "Editó valor del envío (18.000).",
            OrderChangeSummary.ShippingChanged(with, with with { FreightAmount = 18000m }, Colombian));
    }

    // Entrega editable (spec 2026-11-24 §10.4): la fila dice de dónde a dónde cambió la entrega.
    [Fact]
    public void DeliveryChangedDescribesBothSides()
    {
        Assert.Equal(
            "Entrega: Enviar a Cra 7 # 12-30, Medellín → Recoger en tienda",
            OrderChangeSummary.DeliveryChanged(
                OrderDeliveryText.To("Cra 7 # 12-30", "Medellín"), OrderDeliveryText.StorePickup));
        Assert.Equal(
            "Entrega: Recoger en tienda → Enviar a la dirección del cliente",
            OrderChangeSummary.DeliveryChanged(OrderDeliveryText.StorePickup, OrderDeliveryText.CustomerAddress));
        // Una parte a medio llenar dice lo que tiene.
        Assert.Equal(
            "Entrega: Enviar a Calle Mayor 1 → Enviar a otros datos",
            OrderChangeSummary.DeliveryChanged(
                OrderDeliveryText.To("Calle Mayor 1", null), OrderDeliveryText.To(null, " ")));
    }

    // El historial dice a quién se le mandó: quien factura no siempre es quien recibe.
    [Theory]
    [InlineData(QuotationRecipient.Customer, "Enviado por WhatsApp al cliente.")]
    [InlineData(QuotationRecipient.Billing, "Enviado por WhatsApp a facturación.")]
    public void DescribesTheSendWithItsRecipient(QuotationRecipient recipient, string expected) =>
        Assert.Equal(expected, OrderChangeSummary.Sent(recipient));

    // El pedido de la tienda no nace de una solicitud: el historial no nombra ninguna.
    [Fact]
    public void CreatedFromStoreNamesNoRequest() =>
        Assert.Equal("Creado desde la tienda.", OrderChangeSummary.CreatedFromStore());

    [Fact]
    public void AnUnchangedShippingHasNoSummary() =>
        Assert.Null(OrderChangeSummary.ShippingChanged(ShippingDetails.None, ShippingDetails.None, Colombian));

    // Spec 2026-11-10 §3.2: una edición que sube el neto por encima de lo consignado baja el pagado.
    [Fact]
    public void PaymentStatusRecalculatedNamesBothStatusesAndNets() =>
        Assert.Equal(
            "El pago pasó de Pagado a Pago parcial: el neto pasó de 23.800 a 47.600.",
            OrderChangeSummary.PaymentStatusRecalculated(
                new OrderPaymentStatusChange(
                    OrderPaymentStatus.FullPaymentReceived, OrderPaymentStatus.PartialPaymentReceived, 23_800m, 47_600m),
                Colombian));

    // Spec ReteICA por empresa §7.8 (R11): la tasa municipal elegida donde el cliente no es agente lo dice en el
    // resumen de los selectores. El punto doble es lo que da el texto literal del spec (plan, Tarea 13).
    [Fact]
    public void AHandAppliedMunicipalRateSaysTheCustomerIsNotAnAgent()
    {
        Assert.Equal(
            "Cambió las retenciones de ninguna a ReteICA Bogotá; el cliente no está registrado como agente retenedor de ReteICA en Bogotá, D.C..",
            OrderChangeSummary.WithholdingsChanged("ninguna", "ReteICA Bogotá", ("ReteICA", "Bogotá, D.C.")));
        Assert.Equal(
            "Cambió las retenciones de ninguna a Retefuente 2,5 %.",
            OrderChangeSummary.WithholdingsChanged("ninguna", "Retefuente 2,5 %"));
    }

    // El documento no guarda si la fila salió de la regla del cliente o del selector, así que el texto no dice «a mano»:
    // sólo lo que siempre es cierto cuando se dispara (owner 2026-11-14, texto neutro).
    [Fact]
    public void ARateRemovedBecauseTheCustomerIsNotAnAgentExplainsWhy() =>
        Assert.Equal(
            "Se quitó la ReteICA al cambiar la empresa o el municipio: el cliente no es agente retenedor ahí.",
            OrderChangeSummary.WithholdingRemovedNotAgent("ReteICA"));

    // Spec 2026-11-15 §4 (owner 2026-11-15): la decisión del asesor sobre la cuenta de comprador, con los correos.
    [Fact]
    public void TheBuyerAccountChangeNamesBothEmails()
    {
        var ana = new OrderBuyerAccountSnapshot(Guid.CreateVersion7(), "ana@correo.com");
        var luis = new OrderBuyerAccountSnapshot(Guid.CreateVersion7(), "luis@correo.com");
        var deleted = new OrderBuyerAccountSnapshot(Guid.CreateVersion7(), null);

        Assert.Equal(
            "Enlazó el pedido a la cuenta de comprador ana@correo.com.", OrderChangeSummary.BuyerAccountChanged(null, ana));
        Assert.Equal(
            "Cambió la cuenta de comprador de ana@correo.com a luis@correo.com.",
            OrderChangeSummary.BuyerAccountChanged(ana, luis));
        Assert.Equal(
            "Confirmó la cuenta de comprador ana@correo.com: el pedido ya no se enlazará solo a otra.",
            OrderChangeSummary.BuyerAccountChanged(ana, ana));
        Assert.Equal(
            "Quitó la cuenta de comprador ana@correo.com: el pedido ya no se enlazará solo.",
            OrderChangeSummary.BuyerAccountChanged(ana, null));
        Assert.Equal(
            "Quitó la cuenta de comprador: el pedido no tenía y ya no se enlazará solo.",
            OrderChangeSummary.BuyerAccountChanged(null, null));
        Assert.Equal(
            "Cambió la cuenta de comprador de una cuenta ya eliminada a luis@correo.com.",
            OrderChangeSummary.BuyerAccountChanged(deleted, luis));
    }
}
