using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ExportOrderPdfCommand(Guid TenantId, Guid OrderId) : ICommand<OrderPdfExportDto>;

/// <summary>El enlace de descarga y cuándo se generó el documento. La URL es firmada y de vida corta,
/// como la de <see cref="QuotationPdfExportDto"/>: la descarga va directo de R2 al navegador.</summary>
public sealed record OrderPdfExportDto(string Url, DateTimeOffset GeneratedAt);

/// <summary>
/// «Descargar PDF» del pedido (spec 2026-11-16). El documento es el mismo que adjunta
/// <see cref="SendOrderHandler"/>: mismo composer, mismo <see cref="IOrderPdfProvider"/>. Sin caché,
/// por el motivo que da <see cref="OrderPdfProvider"/>: cada descarga genera.
/// </summary>
public sealed class ExportOrderPdfHandler(
    IOrderRepository orders,
    IQuotationRepository quotations,
    IOrderResponseComposer composer,
    IOrderPdfProvider pdfProvider,
    IQuotationPdfStorage storage,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<ExportOrderPdfCommand, OrderPdfExportDto>
{
    public async Task<OrderPdfExportDto> HandleAsync(
        ExportOrderPdfCommand command, CancellationToken cancellationToken)
    {
        // Exportar es leer el pedido en otro formato, el mismo criterio que la cotización con
        // `QuotationRead`. Y en cualquier estado: un anulado sigue siendo el registro de lo acordado;
        // `EnsureSendable` frena el envío, no la lectura.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderRead);

        var order = await orders.FindUntrackedByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        var detail = await composer.ComposeAsync(
            command.TenantId,
            await OrderDetails.LoadAsync(orders, quotations, command.TenantId, order, cancellationToken),
            cancellationToken);
        var storageKey = await pdfProvider.RenderAndSaveAsync(
            command.TenantId, order.Id, detail, cancellationToken);

        var url = await storage.CreateDownloadUrlAsync(
            storageKey, $"Pedido-{order.OrderNumber}.pdf", cancellationToken);

        return new OrderPdfExportDto(url, clock.UtcNow);
    }
}
