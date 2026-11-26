using BuildingBlocks.Domain;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Una factura de Siigo tal como la pantalla la dibuja (spec 2026-11-18 §5.6). BFF: <c>errorCode</c> viaja ya traducido a un código
/// <c>siigo.invoice.*</c> (los pagos que no suman llegan como <c>siigo.invoice.totals_mismatch</c> con <c>expectedPaymentsTotal</c>, para que
/// la pantalla muestre el total de Siigo junto al de QEP) y <c>orderChanged</c> dice que el pedido ya no estaba en la versión armada.
/// <c>orderMarkFailed</c> avisa que la factura existe pero el pedido no lleva su marca (se anuló, alguien lo marcó a mano o el envío murió
/// antes de marcarlo); la siguiente tanda del pedido («Generar») reintenta sólo la marca. Lo pone la emisión, de donde el worker lo copia
/// al ítem de la tanda, y lo calculan al leer la lista, sellar y resolver: sólo en la fila del pedido que existe en Siigo y tiene número,
/// y nunca mientras una tanda sin terminar tenga el pedido (<see cref="SiigoOrderMark"/>). La de flete siempre viaja en <c>false</c>.
/// <c>hasPdf</c> dice si la pantalla ofrece «Descargar PDF»: la factura existe en
/// Siigo con su id (la pantalla no puede deducirlo del estado: una resuelta a mano no tiene id). <c>canSendWhatsApp</c> dice si la pantalla ofrece «Enviar por WhatsApp»: la fila tiene PDF <b>y</b> el pedido se puede entregar hoy
/// (canal que se resuelve y cliente con teléfono). La pantalla no puede deducirlo: no ve los canales del tenant ni la elección guardada, y
/// sólo lo calcula la lista (<c>GET …/siigo-invoice</c>); las respuestas de sellar y resolver lo traen en <c>false</c>. <b>Nunca</b> lleva la petición (<c>request_json</c>), la clave de idempotencia ni datos del cliente.
/// <para>Spec 2026-11-24 §3: <c>awaitingConfirmation</c> dice que no se sabe si Siigo creó la factura (la pantalla no puede pintarla como
/// «Enviando…»); <c>canResend</c> y <c>canResolve</c>, si ofrece «Reintentar» y «Existe en Siigo» / «No existe»; y
/// <c>missingAvailableAt</c>, desde cuándo se puede declarar que no existe, si todavía hay que esperar. Van calculados porque la pantalla
/// no ve desde cuándo se está mandando ni el reloj de 24 horas. <c>canResend</c> va en <c>false</c> mientras una tanda sin terminar tenga el
/// pedido (ítem <c>Pending</c> o <c>Sending</c>): otra tanda respondería 409. <c>canResolveMissing</c> dice si «No existe» se acepta hoy: <c>canResolve</c> y ninguna tanda con el pedido (409);
/// «Existe» sí se acepta. <c>canResolve</c> sólo se ofrece en una <c>Unknown</c> o por confirmar: una
/// que nunca salió el endpoint la acepta, pero ofrecer ahí «Existe / No existe» confunde. La de flete no se ofrece (y el endpoint la
/// rechaza) mientras la del pedido siga abierta: nunca salió.</para>
/// <para>Spec 2026-11-25 (anulación) §5: <c>canConfirmAnnulment</c> dice si la pantalla ofrece «Ya la anulé en Siigo». Sólo en la fila del
/// pedido, nunca mientras una tanda sin terminar lo tenga, y cuando toda factura vigente del pedido se puede verificar (o cuando hay que
/// reintentar la marca). La pantalla no puede deducirlo: no ve si cada fila tiene id de Siigo ni las tandas, ni cruza la de flete.
/// <c>orderUnmarkFailed</c> avisa que la factura del pedido quedó <c>Annulled</c> pero el pedido sigue marcado con ese número: la misma
/// acción lo reintenta. Los dos los calculan la lista y la respuesta de <c>…/annulment</c>; sellar y resolver los traen en
/// <c>false</c>.</para>
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
    bool OrderMarkFailed = false,
    bool HasPdf = false,
    bool CanSendWhatsApp = false,
    bool AwaitingConfirmation = false,
    bool CanResend = false,
    bool CanResolve = false,
    DateTimeOffset? MissingAvailableAt = null,
    bool CanResolveMissing = false,
    bool CanConfirmAnnulment = false,
    bool OrderUnmarkFailed = false)
{
    /// <param name="queued">Una tanda sin terminar tiene el pedido.</param>
    /// <param name="waitsForOrder">Es la de flete y la del pedido sigue abierta.</param>
    /// <param name="canConfirmAnnulment">Ver <see cref="SiigoOrderMark.CanConfirmAnnulment"/>.</param>
    /// <param name="orderUnmarkFailed">La fila quedó anulada y el pedido sigue marcado con su número.</param>
    public static SiigoInvoiceDto From(
        SiigoInvoice invoice,
        DateTimeOffset now,
        bool orderMarkFailed = false,
        bool canSendWhatsApp = false,
        bool queued = false,
        bool waitsForOrder = false,
        bool canConfirmAnnulment = false,
        bool orderUnmarkFailed = false)
    {
        var awaitingConfirmation = invoice.IsAwaitingConfirmation(now);
        var canResolve = !waitsForOrder && invoice.OffersResolution(now);
        return new(
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
            orderMarkFailed,
            invoice.HasPdf,
            canSendWhatsApp && invoice.HasPdf,
            awaitingConfirmation,
            !queued && invoice.CanResend(now),
            canResolve,
            canResolve ? invoice.MissingAvailableAt(now) : null,
            canResolve && !queued,
            canConfirmAnnulment,
            orderUnmarkFailed);
    }
}

/// <summary>Un pago de la factura armada. <paramref name="ProofId"/> dice de qué comprobante sale; <c>null</c> es el saldo.</summary>
public sealed record SiigoDraftPaymentDto(Guid? ProofId, string SiigoPaymentTypeId, decimal Value, DateOnly DueDate);

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
    /// <summary>Lo que falta, para quien lo necesite sin pasar por el 422 (la tanda de facturación guarda sus códigos).</summary>
    public IReadOnlyList<SiigoMissing> Missing { get; } = missing;

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
