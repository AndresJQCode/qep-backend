using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>GET …/orders/{orderId}/siigo-invoice</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record GetSiigoInvoicesQuery(Guid TenantId, Guid OrderId) : IQuery<IReadOnlyList<SiigoInvoiceDto>>;

/// <summary>
/// Spec 2026-11-18 §5.6: las filas vigentes del pedido, una por tipo (la del pedido y, si hay, la de flete), o 404 si nunca se facturó
/// en Siigo. Si el último intento de un tipo fue rechazado, esa fila sigue a la vista para que la pantalla muestre el rechazo. Leer es
/// el momento de la transición perezosa: una <c>Pending</c> de más de 24 horas pasa a <c>Unknown</c>. Nunca devuelve la petición.
/// La fila del pedido que existe en Siigo avisa con <c>orderMarkFailed</c> si el pedido no lleva su marca (<see cref="SiigoOrderMark"/>),
/// y la anulada, con <c>orderUnmarkFailed</c> si todavía la lleva; <c>canConfirmAnnulment</c> dice si se ofrece «Ya la anulé en Siigo».
/// </summary>
public sealed partial class GetSiigoInvoicesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    IClock clock,
    ISiigoInvoiceWhatsApp whatsApp,
    ISiigoInvoiceBatchRepository batches,
    ISiigoOrderSource orders,
    ILogger<GetSiigoInvoicesHandler> logger) : IQueryHandler<GetSiigoInvoicesQuery, IReadOnlyList<SiigoInvoiceDto>>
{
    public async Task<IReadOnlyList<SiigoInvoiceDto>> HandleAsync(GetSiigoInvoicesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var rows = await invoices.ListByOrderAsync(query.TenantId, query.OrderId, cancellationToken);
        if (rows.Count == 0)
        {
            throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceNotFound, "The order has no Siigo invoice.");
        }

        var now = clock.UtcNow;
        var expired = false;
        foreach (var row in rows)
        {
            expired |= row.ExpireIfStale(now);
        }

        if (expired)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Una sola pregunta por petición, y ninguna si ninguna fila tiene PDF: es el camino caliente de la pantalla del pedido.
        var current = SiigoInvoiceSupport.Current(rows);
        var canSend = current.Any(row => row.HasPdf) && await CanSendAsync(query, cancellationToken);
        // Después de leer las filas y antes de leer la marca: el worker marca el pedido mientras la tanda todavía lo tiene, así que una
        // tanda que ya lo soltó no deja un aviso de paso.
        var queued = await batches.FindOpenBatchIdAsync(query.TenantId, query.OrderId, cancellationToken) is not null;
        var result = new List<SiigoInvoiceDto>(current.Count);
        foreach (var row in current)
        {
            var orderMarkFailed = await SiigoOrderMark.IsMissingAsync(orders, row, queued, logger, cancellationToken);
            var waitsForOrder = SiigoInvoiceSupport.IsFreightWaitingForTheOrder(row, current);
            var orderUnmarkFailed = await SiigoOrderMark.IsStillMarkedAfterAnnulmentAsync(orders, row, logger, cancellationToken);
            var canConfirmAnnulment = SiigoOrderMark.CanConfirmAnnulment(row, current, queued, orderUnmarkFailed);
            result.Add(SiigoInvoiceDto.From(
                row, now, orderMarkFailed, canSend, queued, waitsForOrder, canConfirmAnnulment, orderUnmarkFailed));
        }

        return result;
    }

    // `canSendWhatsApp` es una pista opcional: si la pregunta falla por lo que sea, la lista sigue entera (con Created y Rejected a la
    // vista) y la pista queda en false. La cancelación de la petición sí sube (un timeout del HttpClient llega como TaskCanceledException y no cuenta como tal). El log lleva sólo el tipo de la excepción: ni ids, ni teléfono, ni URL.
    private async Task<bool> CanSendAsync(GetSiigoInvoicesQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await whatsApp.CanSendAsync(query.TenantId, query.OrderId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogCanSendFailed(exception.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Checking whether the Siigo invoice can be sent by WhatsApp failed ({ExceptionType}); the list is returned without the hint.")]
    private partial void LogCanSendFailed(string exceptionType);
}
