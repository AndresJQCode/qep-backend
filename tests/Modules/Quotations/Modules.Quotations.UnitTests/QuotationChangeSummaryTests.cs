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
    private static QuotationHeaderSnapshot Snapshot(
        string? shipping,
        bool isStorePickup,
        string? billing = null,
        bool billsToFinalConsumer = false,
        bool? billingWithRetention = null,
        bool? billingVatSurplus = null) => new(
        ValidUntil: new DateOnly(2026, 9, 30),
        PaymentMethod: "Efectivo",
        Notes: null,
        Billing: billing,
        Shipping: shipping,
        BillingAccount: null,
        Currency: QuotationCurrencies.Default,
        IsStorePickup: isStorePickup,
        BillsToFinalConsumer: billsToFinalConsumer,
        BillingWithRetention: billingWithRetention,
        BillingVatSurplus: billingVatSurplus);

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

    // Marcar consumidor final borra la parte de facturacion propia si la habia. Contado por
    // AppendParty eso diria "vuelve a los datos del cliente", que es justo lo que no pasa: la
    // factura sale a otro nombre.
    [Fact]
    public void TurningFinalConsumerOnSaysSoInsteadOfGoingBackToTheCustomer()
    {
        var summary = QuotationChangeSummary.HeaderChanged(
            Snapshot(null, isStorePickup: false, billing: "Sede administrativa|||Carrera 7||"),
            Snapshot(null, isStorePickup: false, billsToFinalConsumer: true));

        Assert.Equal("Editó facturación (consumidor final).", summary);
    }

    // Sin partes de por medio el cambio igual es un cambio: sin este caso, desmarcar el check
    // no dejaba fila en el historial.
    [Fact]
    public void TurningFinalConsumerOffBackToTheCustomerIsAHeaderChange()
    {
        var summary = QuotationChangeSummary.HeaderChanged(
            Snapshot(null, isStorePickup: false, billsToFinalConsumer: true),
            Snapshot(null, isStorePickup: false));

        Assert.Equal("Editó facturación (vuelve a los datos del cliente).", summary);
    }

    [Fact]
    public void TurningFinalConsumerOffToOwnDataSaysOwnData()
    {
        var summary = QuotationChangeSummary.HeaderChanged(
            Snapshot(null, isStorePickup: false, billsToFinalConsumer: true),
            Snapshot(null, isStorePickup: false, billing: "Sede administrativa|||Carrera 7||"));

        Assert.Equal("Editó facturación (ahora con datos propios).", summary);
    }

    // El texto es en español neutro con tuteo, nunca voseo: "Revisa", no "Revisá".
    [Fact]
    public void SendFailedByRecipientTellsAdvisorToCheckContactDataInNeutralSpanish()
    {
        var summary = QuotationChangeSummary.SendFailed(QuotationSendStage.Recipient, whatsAppSkipped: false);

        Assert.Equal(
            "No se pudo enviar la cotización: el cliente no tiene un número de WhatsApp " +
            "válido. Revisa sus datos de contacto.",
            summary);
    }

    // Idem: el mensaje sí salió, pero registrarlo falló. Tuteo, nunca voseo.
    [Fact]
    public void SendFailedByPersistenceTellsAdvisorToCheckWithCustomerInNeutralSpanish()
    {
        var summary = QuotationChangeSummary.SendFailed(QuotationSendStage.Persistence, whatsAppSkipped: false);

        Assert.Equal(
            "No se pudo enviar la cotización: el mensaje salió pero no pudimos registrar " +
            "el envío. Revisa con el cliente antes de reintentar.",
            summary);
    }

    // Spec 2026-10-07, «Cómo se hace explícito que no salió nada», punto 4: el evento sigue siendo
    // Sent/Resent, pero el resumen dice que no salió ningún WhatsApp.
    [Fact]
    public void SentWithoutWhatsAppSaysNoMessageLeft() =>
        Assert.Equal(
            "Marcada como enviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.",
            QuotationChangeSummary.SentWithoutWhatsApp());

    [Fact]
    public void ResentWithoutWhatsAppSaysNoMessageLeft() =>
        Assert.Equal(
            "Marcada como reenviada sin WhatsApp: el envío por WhatsApp está desactivado para la empresa.",
            QuotationChangeSummary.ResentWithoutWhatsApp());

    [Fact]
    public void SendFailedByChannelSendsToTheSettings() =>
        Assert.Equal(
            "No se pudo enviar la cotización: no pudimos leer la configuración de WhatsApp de la " +
            "empresa. Pide a un administrador que la revise en Configuración.",
            QuotationChangeSummary.SendFailed(QuotationSendStage.Channel, whatsAppSkipped: false));

    // Con Disabled el "el mensaje salió" de Persistence es falso: se puede reintentar sin miedo.
    [Fact]
    public void SendFailedByPersistenceWithoutWhatsAppSaysItIsSafeToRetry() =>
        Assert.Equal(
            "No se pudo enviar la cotización: no pudimos registrar el envío. No se mandó ningún " +
            "WhatsApp, así que puedes reintentar.",
            QuotationChangeSummary.SendFailed(QuotationSendStage.Persistence, whatsAppSkipped: true));

    // "Pedido" es masculino (spec 2026-09-14, D7). El sujeto sigue siendo la cotización, por eso
    // "Convertida".
    [Fact]
    public void ConvertingNamesTheOrderInMasculine() =>
        Assert.Equal(
            "Convertida en el pedido PED-2026-0001.",
            QuotationChangeSummary.ConvertedToOrder("PED-2026-0001"));
    // El piso global viaja con el encabezado desde que se persiste con "Guardar cambios". Si el
    // historial no lo cuenta, un guardado que solo cambia el piso no deja rastro -- y el endpoint
    // que se borro si lo dejaba.
    [Fact]
    public void ChoosingAGlobalScaleFloorIsReported()
    {
        var before = Snapshot(null, isStorePickup: false);
        var after = before with { GlobalScaleFloor = 1000 };

        var summary = QuotationChangeSummary.HeaderChanged(before, after);

        Assert.NotNull(summary);
        Assert.Contains("descuento global (escala desde 1000 unidades)", summary);
    }

    [Fact]
    public void ClearingTheGlobalScaleFloorIsReported()
    {
        var before = Snapshot(null, isStorePickup: false) with { GlobalScaleFloor = 1000 };
        var after = before with { GlobalScaleFloor = null };

        var summary = QuotationChangeSummary.HeaderChanged(before, after);

        Assert.NotNull(summary);
        Assert.Contains("quitó el descuento global", summary);
    }

    // El detal viaja con el encabezado desde el 2026-09-23, igual que el piso. Sin contarlo, un
    // guardado que sólo prende el detal no deja rastro, y es el que más cambia el total.
    [Fact]
    public void TurningRetailOnIsReported()
    {
        var before = Snapshot(null, isStorePickup: false);
        var after = before with { IsRetail = true };

        var summary = QuotationChangeSummary.HeaderChanged(before, after);

        Assert.Equal("Editó detal (ninguna línea recibe descuento).", summary);
    }

    [Fact]
    public void TurningRetailOffIsReported()
    {
        var before = Snapshot(null, isStorePickup: false) with { IsRetail = true };
        var after = before with { IsRetail = false };

        var summary = QuotationChangeSummary.HeaderChanged(before, after);

        Assert.Equal("Editó quitó el detal.", summary);
    }

    // Prender el detal se lleva el piso: las dos cosas pasaron y las dos se cuentan, detal
    // primero porque es la causa.
    [Fact]
    public void TurningRetailOnOverAFloorReportsBoth()
    {
        var before = Snapshot(null, isStorePickup: false) with { GlobalScaleFloor = 20 };
        var after = before with { IsRetail = true, GlobalScaleFloor = null };

        var summary = QuotationChangeSummary.HeaderChanged(before, after);

        Assert.Equal(
            "Editó detal (ninguna línea recibe descuento), quitó el descuento global.", summary);
    }

    // El pedido no tiene resumen de encabezado: deja una fila propia con este texto, igual que
    // GlobalScaleFloorChanged.
    [Fact]
    public void RetailChangedNamesTheEffect()
    {
        Assert.Equal(
            "Marcó la cotización como detal: ninguna línea recibe descuento.",
            QuotationChangeSummary.RetailChanged(true));
        Assert.Equal(
            "Quitó el detal: las líneas vuelven a su descuento por escala.",
            QuotationChangeSummary.RetailChanged(false));
    }
}
