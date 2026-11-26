using System.Globalization;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>La entrega de un pedido como la lee una persona en el historial (spec 2026-11-24 §10.4). Sólo se arma por sus
/// tres formas, para que el texto no se escriba en otro lado.</summary>
public sealed record OrderDeliveryText
{
    private OrderDeliveryText(string value) => Value = value;

    public string Value { get; }

    public static OrderDeliveryText StorePickup { get; } = new("Recoger en tienda");

    public static OrderDeliveryText CustomerAddress { get; } = new("Enviar a la dirección del cliente");

    /// <summary>Una parte con datos propios: dirección y ciudad, o lo que tenga de las dos.</summary>
    public static OrderDeliveryText To(string? address, string? city)
    {
        var destination = string.Join(
            ", ", new[] { address, city }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new($"Enviar a {(destination.Length == 0 ? "otros datos" : destination)}");
    }
}

/// <summary>Los textos del historial del pedido (spec 2026-10-15 §4.6). Son hechos que lee una persona:
/// en español, y donde se dirigen a alguien, tuteando.</summary>
public static class OrderChangeSummary
{
    public static string CreatedFromQuotation(string quotationNumber) =>
        Trim($"Creado desde la cotización {quotationNumber}.");

    public static string CreatedManually() => "Creado a mano desde Pedidos.";

    public static string CreatedFromExternal(OrderSource source, string reference) =>
        Trim($"Creado desde {source} ({reference}).");

    /// <summary>Spec 2026-11-14 §4: una edición que llegó de la plataforma, una entrada por grupo aplicado. Sin autor: la hizo
    /// la plataforma, no una persona de QEP.</summary>
    public static string EditedFromExternal(OrderSource source, ExternalOrderEditGroup group) => group switch
    {
        ExternalOrderEditGroup.Lines => Trim($"{source} editó los productos."),
        ExternalOrderEditGroup.Shipping => Trim($"{source} editó el envío."),
        ExternalOrderEditGroup.Party => Trim($"{source} editó la dirección de envío."),
        ExternalOrderEditGroup.BuyerNotes => Trim($"{source} editó la nota del cliente."),
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown external edit group."),
    };

    // El pedido de la tienda sale directo del checkout: no hay solicitud que nombrar.
    public static string CreatedFromStore() => "Creado desde la tienda.";

    /// <summary>Mayorista fase 3: el pedido que creó la orden de compra de un distribuidor vinculado.</summary>
    public static string CreatedFromPurchaseOrder(string purchaseOrderNumber) =>
        Trim($"Creado desde la orden de compra {purchaseOrderNumber} del distribuidor.");

    /// <summary>D-1: la aprobación del pedido mayorista; desde acá no se edita y la orden del distribuidor queda confirmada.</summary>
    public static string WholesaleApproved() => "Aprobó el pedido del distribuidor.";

    /// <summary>D-4: el precio por línea de un pedido mayorista.</summary>
    public static string ItemUnitPriceChanged(string productName, decimal from, decimal to, HistoryFormat format) =>
        Trim($"Cambió el precio de {productName}: {format.Number(from)} → {format.Number(to)}.");

    public static string ItemAdded(string productName, decimal quantity, HistoryFormat format) =>
        QuotationChangeSummary.ItemAdded(productName, quantity, format);

    public static string ItemQuantityChanged(string productName, decimal from, decimal to, HistoryFormat format) =>
        QuotationChangeSummary.ItemQuantityChanged(productName, from, to, format);

    public static string ItemRemoved(string productName) => QuotationChangeSummary.ItemRemoved(productName);

    /// <summary>Spec 2026-11-21: el regalo entra y sale por su propio camino, y el historial lo dice con esa palabra.</summary>
    public static string GiftAdded(string productName, decimal quantity, HistoryFormat format) =>
        Trim($"Agregó de regalo {productName} x{format.Number(quantity)}.");

    public static string GiftRemoved(string productName) => Trim($"Quitó el regalo {productName}.");

    /// <summary>Spec 2026-11-23: «Editar pedido» cambia la cantidad de un regalo sin quitarlo y volverlo a agregar.</summary>
    public static string GiftQuantityChanged(string productName, decimal from, decimal to, HistoryFormat format) =>
        Trim($"Cambió la cantidad del regalo {productName}: {format.Number(from)} → {format.Number(to)}.");

    public static string RetailChanged(bool isRetail) => isRetail
        ? "Marcó el pedido como detal: ninguna línea recibe descuento."
        : "Quitó el detal: las líneas vuelven a su descuento por escala.";

    public static string GlobalScaleFloorChanged(int? floor, HistoryFormat format) =>
        QuotationChangeSummary.GlobalScaleFloorChanged(floor, format);

    public static string NotesChanged() => "Editó las notas.";

    /// <summary>Spec 2026-11-16 §3 (F-6): las dos fechas en ISO, con cultura invariante para que el texto no dependa del
    /// servidor.</summary>
    public static string OrderDateChanged(DateOnly from, DateOnly to) => string.Create(
        CultureInfo.InvariantCulture, $"Cambió la fecha del pedido de {from:yyyy-MM-dd} a {to:yyyy-MM-dd}.");

    /// <summary>Spec 2026-11-14 §5: el asesor corrigió la parte de envío de un pedido de Shopify (viaja a Shopify).</summary>
    public static string ShippingPartyEdited() => "Editó la dirección de envío.";

    /// <summary>Spec 2026-11-14 §5: el asesor corrigió la nota del cliente de un pedido de Shopify (viaja a Shopify).</summary>
    public static string BuyerNotesEdited() => "Editó la nota del cliente.";

    /// <summary>El cambio de envío de un pedido pendiente (spec 2026-10-17 §5.4), con los textos de la
    /// cotización. <c>null</c> si nada de lo que se cuenta cambió.</summary>
    public static string? ShippingChanged(ShippingDetails before, ShippingDetails after, HistoryFormat format)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<string>(4);
        QuotationChangeSummary.AppendFreight(changes, before, after, format);
        return changes.Count == 0 ? null : Trim($"Editó {string.Join(", ", changes)}.");
    }

    /// <summary>El cambio de recoger o enviar, o de dirección, de un pedido (spec 2026-11-24 §10.4).</summary>
    public static string DeliveryChanged(OrderDeliveryText before, OrderDeliveryText after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return Trim($"Entrega: {before.Value} → {after.Value}");
    }

    /// <summary>A quién se le factura, tal como lo lee una persona: el nombre de la parte propia, «consumidor
    /// final», o «los datos del cliente» (con su razón social si es el caso).</summary>
    public static string BillingLabel(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.BillsToFinalConsumer)
        {
            return "consumidor final";
        }

        if (order.Billing is { } party)
        {
            return string.IsNullOrWhiteSpace(party.Name) ? "otros datos" : party.Name;
        }

        return order.BillingUsesBusinessName ? "los datos del cliente (razón social)" : "los datos del cliente";
    }

    /// <summary>El cambio de facturación de un pedido pendiente (spec 2026-10-26). Si a quién se factura no
    /// cambió y sólo se tocó un dato de la parte, lo dice sin repetir el nombre.</summary>
    public static string BillingChanged(string before, string after) =>
        string.Equals(before, after, StringComparison.Ordinal)
            ? Trim($"Editó los datos de facturación de {after}.")
            : Trim($"Cambió la facturación de {before} a {after}.");

    /// <summary>La cuenta de cobro como la reconoce una persona: banco y número. <c>null</c> sin cuenta.</summary>
    public static string? BillingAccountLabel(QuotationBillingAccount? account) =>
        account switch
        {
            null => null,
            // Empresa sin cuenta (spec 2026-11-17, R3).
            { HasBankAccount: false } => "sin cuenta",
            _ => $"{account.BankName} {account.AccountNumber}",
        };

    /// <summary>La empresa y la cuenta con que se cobra un pedido pendiente (spec impuestos por tipo D22).</summary>
    public static string BillingAccountChanged(string? before, string after) =>
        before is null
            ? Trim($"Fijó la cuenta de cobro en {after}.")
            : Trim($"Cambió la cuenta de cobro de {before} a {after}.");

    /// <summary>Las tasas de retención vigentes del pedido, por nombre y en su orden; «ninguna» sin tasas.</summary>
    public static string WithholdingsLabel(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var names = order.Withholdings
            .OrderBy(withholding => withholding.Position)
            .Select(withholding => withholding.Name)
            .ToArray();
        return names.Length == 0 ? "ninguna" : string.Join(", ", names);
    }

    /// <summary>Los selectores de retención de un pedido pendiente (spec impuestos por tipo D21). Con
    /// <paramref name="notAgent"/>, la tasa municipal elegida es para un municipio donde el cliente no es agente (aplicación
    /// a mano, spec ReteICA por empresa R11) y el texto lo dice; el punto doble con un municipio que termina en punto
    /// («Bogotá, D.C.») es lo que da el literal del spec.</summary>
    public static string WithholdingsChanged(string before, string after, (string Type, string City)? notAgent = null) =>
        Trim(notAgent is { } manual
            ? $"Cambió las retenciones de {before} a {after}; el cliente no está registrado como agente retenedor de {manual.Type} en {manual.City}."
            : $"Cambió las retenciones de {before} a {after}.");

    /// <summary>La re-resolución quitó una tasa municipal porque el cliente no es agente en el municipio. No dice «a mano»:
    /// no se guarda si la fila salió de la regla o del selector (owner 2026-11-14; spec §7.8, R11).</summary>
    public static string WithholdingRemovedNotAgent(string type) =>
        Trim($"Se quitó la {type} al cambiar la empresa o el municipio: el cliente no es agente retenedor ahí.");

    /// <summary>El PDF salió por WhatsApp. Dice a quién porque quien factura no siempre es quien recibe.</summary>
    public static string Sent(QuotationRecipient recipient, string? contactName = null) => recipient switch
    {
        QuotationRecipient.Billing => "Enviado por WhatsApp a facturación.",
        QuotationRecipient.Contact when contactName is not null => Trim($"Enviado por WhatsApp a {contactName}."),
        _ => "Enviado por WhatsApp al cliente.",
    };

    public static string Cancelled(string reason) => Trim($"Anulado: {reason}");

    /// <summary>La anulación de la plataforma sobre un pedido ya preparado (spec 2026-11-10 §3.1): el pedido conserva la
    /// marca y el historial avisa que hay un paquete que recuperar.</summary>
    public static string CancelledWhilePrepared(string reason) =>
        Trim($"Anulado con el pedido ya preparado: {reason}. Hay un paquete despachado que recuperar.");

    public static string Invoiced() => "Marcado como facturado.";

    /// <summary>La factura la emitió un proveedor externo: el historial dice cuál y con qué número, en vez del genérico
    /// «Marcado como facturado.» de la marca manual.</summary>
    public static string InvoicedExternally(string provider, string number) =>
        Trim($"Facturado en {ProviderDisplayName(provider)}, factura {number}.");

    /// <summary>La factura se anuló en el proveedor externo y el pedido perdió su marca (spec 2026-11-25 §4).</summary>
    public static string ExternalInvoiceAnnulled(string provider, string number) =>
        Trim($"Se anuló en {ProviderDisplayName(provider)} la factura {number}; el pedido quedó sin facturar.");

    private static string ProviderDisplayName(string provider) =>
        string.Equals(provider, "siigo", StringComparison.OrdinalIgnoreCase) ? "Siigo" : provider;

    public static string InvoicingReverted() => "Se revirtió la facturación.";

    public static string PaidMarked() => "Marcado como pagado.";

    public static string PaidUnmarked() => "Se deshizo el pagado.";

    public static string Prepared() => "Marcado como preparado.";

    public static string PreparationUndone() => "Se deshizo el preparado.";

    public static string Archived() => "Archivado.";

    public static string ArchivedAutomatically() => "Archivado automáticamente: quedó pagado y preparado.";

    public static string Unarchived() => "Desarchivado.";

    public static string PaymentProofsChanged(int added, int corrected, int removed)
    {
        var parts = new List<string>(3);
        if (added > 0)
        {
            parts.Add($"{added} comprobante(s) nuevo(s)");
        }

        if (corrected > 0)
        {
            parts.Add($"{corrected} corregido(s)");
        }

        if (removed > 0)
        {
            parts.Add($"{removed} retirado(s)");
        }

        return Trim($"Comprobantes: {string.Join(", ", parts)}.");
    }

    /// <summary>El pago lo cambió el sistema, no una persona (spec 2026-11-10 §3.2): nombra los dos estados y los dos
    /// netos para que se entienda por qué.</summary>
    public static string PaymentStatusRecalculated(OrderPaymentStatusChange change, HistoryFormat format)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(format);
        return Trim(
            $"El pago pasó de {ExportStatusLabels.For(change.From)} a {ExportStatusLabels.For(change.To)}: " +
            $"el neto pasó de {format.Number(change.PreviousNetTotal)} a {format.Number(change.NetTotal)}.");
    }

    /// <summary>Spec 2026-11-15 §4: la decisión del asesor sobre la cuenta de comprador. Lleva los correos (lo que ya ve en el
    /// detalle) para que la entrada se siga entendiendo si la cuenta se borra después; los ids van en la auditoría. Una cuenta
    /// anterior que ya no existe se nombra como eliminada.</summary>
    public static string BuyerAccountChanged(OrderBuyerAccountSnapshot? previous, OrderBuyerAccountSnapshot? current)
    {
        var before = previous is null ? null : previous.Email ?? "una cuenta ya eliminada";
        return (before, current?.Email) switch
        {
            (null, null) => "Quitó la cuenta de comprador: el pedido no tenía y ya no se enlazará solo.",
            (null, { } email) => Trim($"Enlazó el pedido a la cuenta de comprador {email}."),
            ({ } old, null) => Trim($"Quitó la cuenta de comprador {old}: el pedido ya no se enlazará solo."),
            ({ } old, { } email) when previous!.Id == current!.Id =>
                Trim($"Confirmó la cuenta de comprador {email}: el pedido ya no se enlazará solo a otra."),
            ({ } old, { } email) => Trim($"Cambió la cuenta de comprador de {old} a {email}."),
        };
    }

    private static string Trim(string summary) =>
        summary.Length <= OrderHistoryEntry.DetailsMaxLength
            ? summary
            : summary[..(OrderHistoryEntry.DetailsMaxLength - 1)] + "…";
}
