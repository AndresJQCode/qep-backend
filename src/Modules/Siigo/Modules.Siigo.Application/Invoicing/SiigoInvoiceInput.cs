namespace Modules.Siigo.Application.Invoicing;

// Spec 2026-11-18 §5.5 y §6: la foto del pedido que el armador necesita. La llena el adaptador de Bootstrapper desde el contrato de
// Quotations (ExternalInvoicingOrder); Siigo nunca referencia tipos de Quotations (SiigoLayerTests), así que los enums de documento y
// de persona se declaran acá y el adaptador los traduce por nombre, con una prueba de paridad que los ata.

/// <summary>Natural o jurídica, como la parte facturada del pedido. Se traduce por nombre desde Quotations.</summary>
public enum SiigoPersonKind
{
    Natural,
    Juridica,
}

/// <summary>El tipo de documento de la parte facturada, los siete que conoce QEP. Se traduce por nombre desde Quotations. Siigo sólo
/// tiene código para cuatro (<see cref="SiigoIdentificationTypes"/>).</summary>
public enum SiigoIdentificationKind
{
    Nit,
    Cc,
    Ce,
    Pasaporte,
    Nif,
    Nie,
    TaxId,
}

/// <summary>Una línea del pedido. <paramref name="Subtotal"/> es el de la línea <b>con el descuento aplicado y sin IVA</b>
/// (<c>OrderItem.Subtotal</c>); el precio unitario que va a Siigo se deriva de él. Un regalo (<paramref name="IsGift"/>) trae subtotal
/// e IVA en 0 y su precio de lista con IVA incluido en <paramref name="ListUnitPrice"/>: de ahí sale la base del obsequio.</summary>
public sealed record SiigoInvoiceLineInput(
    Guid VariantId,
    string Name,
    decimal Quantity,
    decimal Subtotal,
    decimal DiscountPercentage,
    int TaxPercentage,
    decimal TaxAmount,
    bool IsGift = false,
    decimal ListUnitPrice = 0m);

/// <summary>Una retención <b>aplicada</b> del pedido (<paramref name="Type"/> es <c>Retefuente</c> o <c>ReteICA</c>; otro tipo es un
/// faltante). Sólo viaja el vínculo: el monto lo calcula Siigo.</summary>
public sealed record SiigoWithholdingInput(Guid TaxRateId, string Name, string Type, decimal Amount, decimal Percentage = 0m);

/// <summary>Un comprobante de pago del pedido: cada uno es un pago de la factura, por su monto.</summary>
public sealed record SiigoPaymentProofInput(Guid ProofId, decimal Amount);

/// <summary>
/// La ciudad que el cliente escribió fuera de Colombia y que no se pudo vincular a la lista de Siigo (spec 2026-11-26).
/// <paramref name="CountryCode"/> es el país de QEP en mayúscula; <paramref name="Reportable"/> es <c>false</c> cuando el país ni siquiera
/// está en el catálogo, y entonces no se anota en la bandeja del operador. Es dato del cliente: nunca se imprime ni se loguea.
/// </summary>
public sealed record SiigoUnlinkedCity(string CountryCode, string CityText, bool Reportable)
{
    public override string ToString() => "SiigoUnlinkedCity { }";
}

/// <summary>La parte a la que se factura, ya resuelta (pedido o ficha del cliente). Lleva datos del cliente: nunca se imprime ni se
/// loguea (el <c>ToString</c> no escribe nada). <paramref name="SiigoCountryCode"/> es el código de país de la lista de Siigo (<c>Us</c>)
/// y sólo viaja fuera de Colombia; <paramref name="UnlinkedCity"/> es la ciudad escrita que no se vinculó (el armador reporta
/// <c>customer_city_unlinked</c> en vez de <c>customer_city_missing</c>).</summary>
public sealed record SiigoBilledPartyInput(
    bool IsFinalConsumer,
    SiigoPersonKind? PersonType,
    SiigoIdentificationKind? IdentificationType,
    string Identification,
    string Name,
    string? Address,
    string? CityCode,
    string? StateCode,
    string? Email,
    SiigoPhone? Phone,
    string? SiigoCountryCode = null,
    SiigoUnlinkedCity? UnlinkedCity = null)
{
    public override string ToString() => "SiigoBilledPartyInput { }";
}

/// <summary>El pedido tal como Siigo lo necesita para armar la factura. <paramref name="CompanyId"/> elige la conexión;
/// <paramref name="OrderVersion"/> es la versión del pedido al armar (la fila guarda si cambió después, spec §5.4);
/// <paramref name="AdvisorMembershipId"/> es la membresía del asesor: su vínculo da el vendedor (spec 2026-11-20, vendedores vinculados);
/// <paramref name="AdvisorLabel"/>, su nombre o correo para el faltante (null si ya no resuelve).</summary>
public sealed record SiigoInvoiceInput(
    Guid OrderId,
    string OrderNumber,
    string Currency,
    DateOnly Today,
    bool CashOnDelivery,
    IReadOnlyList<SiigoInvoiceLineInput> Lines,
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    int FreightTaxPercentage,
    decimal ProductsTotal,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    IReadOnlyList<SiigoWithholdingInput> Withholdings,
    IReadOnlyList<SiigoPaymentProofInput> PaymentProofs,
    string? BillingAccountKey,
    string? BuyerNotes,
    SiigoBilledPartyInput BilledParty,
    Guid? CompanyId,
    long OrderVersion,
    Guid AdvisorMembershipId,
    string? AdvisorLabel,
    string? ShippingAddress = null,
    string? ShippingCity = null);
