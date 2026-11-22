using BuildingBlocks.Application;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>GET …/orders/{orderId}/siigo-invoice</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record GetSiigoInvoicesQuery(Guid TenantId, Guid OrderId) : IQuery<IReadOnlyList<SiigoInvoiceDto>>;

/// <summary>
/// Spec 2026-11-18 §5.6: las filas vigentes del pedido, una por tipo (la del pedido y, si hay, la de flete), o 404 si nunca se facturó
/// en Siigo. Si el último intento de un tipo fue rechazado, esa fila sigue a la vista para que la pantalla muestre el rechazo. Leer es
/// el momento de la transición perezosa: una <c>Pending</c> de más de 24 horas pasa a <c>Unknown</c>. Nunca devuelve la petición.
/// </summary>
public sealed class GetSiigoInvoicesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    IClock clock,
    ISiigoInvoiceWhatsApp whatsApp) : IQueryHandler<GetSiigoInvoicesQuery, IReadOnlyList<SiigoInvoiceDto>>
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
        var canSend = current.Any(row => row.HasPdf)
            && await whatsApp.CanSendAsync(query.TenantId, query.OrderId, cancellationToken);
        return [.. current.Select(row => SiigoInvoiceDto.From(row, canSendWhatsApp: canSend))];
    }
}
