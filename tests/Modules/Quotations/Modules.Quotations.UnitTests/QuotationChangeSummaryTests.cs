using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El historial tiene que decir la verdad sobre la entrega. Pasar a "recoger en tienda" borra la
/// parte de envio, y sin un caso propio eso se leia como "envio (vuelve a los datos del
/// cliente)": justo lo contrario de lo que paso.
/// </summary>
public sealed class QuotationChangeSummaryTests
{
    private static QuotationHeaderSnapshot Snapshot(string? shipping, bool isStorePickup) => new(
        ValidUntil: new DateOnly(2026, 9, 30),
        PaymentMethod: "Efectivo",
        Notes: null,
        Billing: null,
        Shipping: shipping,
        BillingAccount: null,
        Currency: QuotationCurrencies.Default,
        IsStorePickup: isStorePickup);

    [Fact]
    public void TurningStorePickupOnSaysSoInsteadOfGoingBackToTheCustomer()
    {
        var summary = QuotationChangeSummary.HeaderChanged(
            Snapshot("Bodega|||Zona Franca||", isStorePickup: false),
            Snapshot(null, isStorePickup: true));

        Assert.NotNull(summary);
        Assert.Contains("recoger en tienda", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("vuelve a los datos del cliente", summary, StringComparison.Ordinal);
    }

    // Sin partes de por medio el cambio igual es un cambio: sin este caso, prender el switch
    // no dejaba fila en el historial.
    [Fact]
    public void TurningStorePickupOffIsAHeaderChange()
    {
        var summary = QuotationChangeSummary.HeaderChanged(
            Snapshot(null, isStorePickup: true),
            Snapshot(null, isStorePickup: false));

        Assert.NotNull(summary);
        Assert.Contains("envío", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingChangedStillWritesNothing()
    {
        var summary = QuotationChangeSummary.HeaderChanged(
            Snapshot(null, isStorePickup: true),
            Snapshot(null, isStorePickup: true));

        Assert.Null(summary);
    }
}
