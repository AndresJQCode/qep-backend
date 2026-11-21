using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Una línea del pedido para facturar. <paramref name="Subtotal"/> es el de la línea con el descuento aplicado y sin IVA.</summary>
public sealed record ExternalInvoicingLine(
    Guid VariantId,
    string Name,
    decimal Quantity,
    decimal Subtotal,
    decimal DiscountPercentage,
    int TaxPercentage,
    decimal TaxAmount);

/// <summary>Una retención <b>aplicada</b> del pedido (las que no aplicaron no viajan).</summary>
public sealed record ExternalInvoicingWithholding(Guid TaxRateId, string Name, string Type, decimal Amount);

/// <summary>Un comprobante de pago: cada uno es un pago de la factura.</summary>
public sealed record ExternalInvoicingProof(Guid ProofId, decimal Amount);

/// <summary>
/// A quién se le factura, ya resuelto con la precedencia de <see cref="OrderBilledParty.SourceFor"/>: consumidor final, la parte de
/// facturación del pedido o la ficha del cliente, con lo que falte de la primera tomado de la segunda (spec 2026-11-18 §6.2). Lleva
/// datos del cliente: el <c>ToString</c> no los escribe.
/// </summary>
public sealed record ExternalInvoicingParty(
    bool IsFinalConsumer,
    PartyPersonType? PersonType,
    PartyIdentificationType? IdentificationType,
    string Identification,
    string Name,
    string? Address,
    Guid? CityId,
    Guid? DepartmentId,
    string? Country,
    string? Email,
    string? Phone)
{
    public override string ToString() => "ExternalInvoicingParty { }";
}

/// <summary>
/// La foto del pedido que un facturador externo necesita (spec 2026-11-18 §5.5). <paramref name="ProductsTotal"/> es la suma de las
/// líneas con IVA (sin envío); <paramref name="Total"/> incluye el envío; <paramref name="NetTotal"/> es <c>Total</c> menos las
/// retenciones. La cuenta de cobro viaja como banco y número: la clave con que cada integración la vincula es suya. <paramref name="Version"/>
/// es la del pedido al armar. <paramref name="AdvisorMembershipId"/> es la membresía del asesor del pedido: con ella Siigo busca el vendedor
/// vinculado (spec 2026-11-20, vendedores vinculados); <paramref name="AdvisorLabel"/> es su nombre o, sin nombre, su correo, para el texto
/// del faltante (null si ya no resuelve).
/// </summary>
public sealed record ExternalInvoicingOrder(
    Guid OrderId,
    string OrderNumber,
    string Currency,
    DateOnly Today,
    bool CashOnDelivery,
    IReadOnlyList<ExternalInvoicingLine> Lines,
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    int FreightTaxPercentage,
    decimal ProductsTotal,
    decimal Total,
    decimal RetentionAmount,
    decimal NetTotal,
    IReadOnlyList<ExternalInvoicingWithholding> Withholdings,
    IReadOnlyList<ExternalInvoicingProof> PaymentProofs,
    Guid? CompanyId,
    string? BillingAccountBankName,
    string? BillingAccountNumber,
    string? BuyerNotes,
    ExternalInvoicingParty BilledParty,
    long Version,
    Guid AdvisorMembershipId,
    string? AdvisorLabel,
    string? ShippingAddress = null,
    string? ShippingCity = null);

/// <summary>
/// El puerto que un facturador externo (Siigo) usa sobre los pedidos (spec 2026-11-18 §5.5). El adaptador vive en Bootstrapper; este
/// puerto es de Quotations y lo implementa <see cref="ExternalOrderInvoicingService"/>.
/// </summary>
public interface IExternalOrderInvoicing
{
    /// <summary>
    /// La foto del pedido, o <c>null</c> si no es del tenant. Aplica las mismas precondiciones que la marca manual y en el mismo orden:
    /// no anulado, <b>no facturado</b> (un pedido marcado a mano no se factura de nuevo), pagado si el tenant lo exige, tipo de persona y
    /// campos del cliente facturado. Cada una es el mismo 422 de dominio de <c>InvoiceOrderHandler</c>.
    /// </summary>
    Task<ExternalInvoicingOrder?> GetForInvoicingAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>La versión actual del pedido, o <c>null</c> si no es del tenant. Sin las precondiciones de <see cref="GetForInvoicingAsync"/>:
    /// el facturador la compara con la que armó, después de crear la factura, para avisar que el pedido cambió.</summary>
    Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Marca el pedido como facturado por <paramref name="provider"/> (<see cref="Order.InvoiceExternally"/>), con historial
    /// <c>Invoiced</c> y la auditoría <c>quotation.order.invoiced</c> con <c>provider</c> en <c>changedFields</c>. Idempotente: con la
    /// misma marca no hace nada. Reintenta una vez ante <c>RequestConcurrencyException</c>.
    /// </summary>
    Task MarkInvoicedAsync(
        Guid tenantId,
        Guid orderId,
        string provider,
        string number,
        string externalId,
        Guid memberId,
        CancellationToken cancellationToken);
}
