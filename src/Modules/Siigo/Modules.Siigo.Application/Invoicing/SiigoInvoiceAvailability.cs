using BuildingBlocks.Application;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>GET …/orders/{orderId}/siigo-invoice/availability</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record SiigoInvoiceAvailabilityQuery(Guid TenantId, Guid OrderId) : IQuery<SiigoInvoiceAvailabilityDto>;

/// <summary>
/// <see cref="Available"/> dice si el pedido se puede mandar a Siigo <b>ahora</b>; si no, <see cref="Reason"/> es el código
/// (<c>siigo.invoice.currency_unsupported</c>, <c>siigo.invoice.no_company</c>, <c>siigo.invoice.no_connection</c> o
/// <c>siigo.connection_unavailable</c>) y es <c>null</c> cuando sí. BFF (spec 2026-11-24 §1): <see cref="QueuedBatchId"/> es la tanda sin
/// terminar que tiene a ese pedido, para que la pantalla del pedido retome el seguimiento al recargar.
/// </summary>
public sealed record SiigoInvoiceAvailabilityDto(bool Available, string? Reason, Guid? QueuedBatchId = null);

/// <summary>
/// La pantalla del pedido sólo ofrece «Facturar en Siigo» si la conexión <b>de la empresa de ese pedido</b> sirve: que haya otra conexión
/// activa no alcanza, porque el envío sale por la que eligió esa empresa. Es la misma resolución que hace el envío (empresa de la cuenta
/// de cobro → configuración → credenciales), para que el botón y el envío nunca discrepen, y no escribe ni llama a Siigo. Lo que no es de
/// la conexión (pedido sin pagar, datos del cliente…) no se evalúa acá: sigue siendo el 422 de la vista previa.
/// </summary>
public sealed class SiigoInvoiceAvailabilityHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoOrderSource orders,
    ISiigoSettingsRepository settingsRepository,
    ISiigoConnectionDirectory connections,
    ISiigoInvoiceBatchRepository batches) : IQueryHandler<SiigoInvoiceAvailabilityQuery, SiigoInvoiceAvailabilityDto>
{
    public async Task<SiigoInvoiceAvailabilityDto> HandleAsync(SiigoInvoiceAvailabilityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);

        var queuedBatchId = await batches.FindOpenBatchIdAsync(query.TenantId, query.OrderId, cancellationToken);
        SiigoInvoicingContext context;
        try
        {
            context = await SiigoInvoiceSupport.LoadAsync(orders, settingsRepository, query.TenantId, query.OrderId, cancellationToken);
        }
        catch (SiigoDomainException exception) when (exception.Code is
            SiigoErrorCodes.InvoiceCurrencyUnsupported or SiigoErrorCodes.InvoiceNoCompany or SiigoErrorCodes.InvoiceNoConnection)
        {
            return new SiigoInvoiceAvailabilityDto(false, exception.Code, queuedBatchId);
        }

        var resolution = await connections.ResolveAsync(query.TenantId, context.Settings.ConnectionId, cancellationToken);
        return resolution.Credentials is null
            ? new SiigoInvoiceAvailabilityDto(false, SiigoErrorCodes.ConnectionUnavailable, queuedBatchId)
            : new SiigoInvoiceAvailabilityDto(true, null, queuedBatchId);
    }
}
