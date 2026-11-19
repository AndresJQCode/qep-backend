using BuildingBlocks.Domain;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Una factura de Siigo tal como la pantalla la dibuja (spec 2026-11-18 §5.6). BFF: <c>errorCode</c> viaja ya traducido a un código
/// <c>siigo.invoice.*</c> (los pagos que no suman llegan como <c>siigo.invoice.totals_mismatch</c> con <c>expectedPaymentsTotal</c>, para que
/// la pantalla muestre el total de Siigo junto al de QEP) y <c>orderChanged</c> dice que el pedido ya no estaba en la versión armada.
/// <c>orderMarkFailed</c> avisa que la factura existe pero el pedido no se pudo marcar (se anuló o alguien lo marcó a mano): la pantalla
/// lo muestra y el siguiente «Generar» reintenta sólo la marca. <b>Nunca</b> lleva la petición (<c>request_json</c>), la clave de idempotencia ni datos del cliente.
/// </summary>
public sealed record SiigoInvoiceDto(
    Guid Id,
    string Kind,
    string Status,
    string? Number,
    string? Cufe,
    decimal? SiigoTotal,
    string? ErrorCode,
    string? ErrorDetail,
    decimal? ExpectedPaymentsTotal,
    bool OrderChanged,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool OrderMarkFailed = false)
{
    public static SiigoInvoiceDto From(SiigoInvoice invoice, bool orderMarkFailed = false) =>
        new(
            invoice.Id,
            invoice.Kind.ToString(),
            invoice.Status.ToString(),
            invoice.Number,
            invoice.Cufe,
            invoice.SiigoTotal,
            invoice.ErrorCode,
            invoice.ErrorDetail,
            invoice.ExpectedPaymentsTotal,
            invoice.OrderChanged,
            invoice.Attempts,
            invoice.CreatedAt,
            invoice.UpdatedAt,
            orderMarkFailed);
}

/// <summary>Un pago de la factura armada. <paramref name="ProofId"/> dice de qué comprobante sale; <c>null</c> es el saldo.</summary>
public sealed record SiigoDraftPaymentDto(Guid? ProofId, string SiigoPaymentTypeId, decimal Value, DateOnly? DueDate);

/// <summary>Una factura armada que todavía no se mandó: su tipo, lo que suman sus pagos y los pagos propuestos.</summary>
public sealed record SiigoInvoiceDraftDto(string Kind, decimal Total, IReadOnlyList<SiigoDraftPaymentDto> Payments);

/// <summary>Un faltante: el código lo traduce la pantalla (<see cref="SiigoMissing"/>).</summary>
public sealed record SiigoMissingDto(string Code, string? Reference, string? Label);

/// <summary>Los ítems no cuadran con el total del pedido en más de un peso (spec §6.4).</summary>
public sealed record SiigoTotalsMismatchDto(decimal QepTotal, decimal ComputedTotal);

/// <summary><c>GET …/preview</c>: la factura armada, los faltantes y la propuesta de pagos, sin escribir nada ni llamar a Siigo.</summary>
public sealed record SiigoInvoicePreviewDto(
    IReadOnlyList<SiigoInvoiceDraftDto> Drafts,
    IReadOnlyList<SiigoMissingDto> Missing,
    SiigoTotalsMismatchDto? TotalsMismatch);

/// <summary>
/// 422 <c>siigo.invoice.not_ready</c> (spec §5.4, paso 2): el pedido no está listo y <c>details.missing</c> trae <b>todo</b> lo que falta,
/// junto. Nada salió a Siigo.
/// </summary>
public sealed class SiigoInvoiceNotReadyException(IReadOnlyList<SiigoMissing> missing)
    : DomainException(SiigoErrorCodes.InvoiceNotReady, "The order is not ready to be invoiced in Siigo."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["details"] = new { missing = missing.Select(value => new SiigoMissingDto(value.Code, value.Reference, value.Label)).ToArray() },
    };
}

/// <summary>422 <c>siigo.invoice.totals_mismatch</c> antes de crear: los ítems no cuadran con el pedido; <c>details</c> trae los dos
/// valores.</summary>
public sealed class SiigoInvoiceTotalsMismatchException(decimal qepTotal, decimal computedTotal)
    : DomainException(SiigoErrorCodes.InvoiceTotalsMismatch, "The items of the order do not add up to its total."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["details"] = new { qepTotal, computedTotal },
    };
}
