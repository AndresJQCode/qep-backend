using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de pedidos en el worker (D7, D8). Mismo esquema que
/// <see cref="QuotationsExportProcessor"/>: lotes de mil con keyset y el filtro del listado
/// (<see cref="OrderListing"/> y el <c>Filtered</c> de OrderRepository), streaming y subida con el id
/// del job. El lote de lectura y escritura lo comparten los dos en <see cref="ExportBatchLoop"/>.
/// </summary>
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
{
    public const string SheetName = "Ventas";

    public const string FilePrefix = "ventas";

    /// <summary>
    /// Las de la tabla de pedidos en su orden (order-table.tsx: Venta, Cliente, Asesora, Fecha, Pago,
    /// Estado, Total), con la moneda aparte del total y "Asesor" como en el Excel de cotizaciones.
    /// "Pago" replica el respaldo de la tabla: la forma de pago o, mientras llegue vacía, la etiqueta del
    /// estado del pago. Los estados van con la etiqueta de la pantalla (spec 2026-09-13, A7).
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("Venta", 18),
        new("Cliente", 40),
        new("Asesor", 32),
        new("Fecha", 34),
        new("Pago", 24),
        new("Estado", 12),
        new("Moneda", 10),
        new("Total", 16),
    ];

    public ExportJobKind Kind => ExportJobKind.Sales;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<OrdersExportFilters>(job);
        var (status, paymentStatus) = ParseStatuses(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        var generatedAt = clock.UtcNow;

        using var workbook = writer.Create(SheetName, Columns);
        var rowCount = await ExportBatchLoop.WriteAllAsync<OrderWithQuotation, OrderExportCursor>(
            workbook,
            (after, limit, ct) => repository.ListForExportAsync(
                job.TenantId,
                filters.ClientId,
                clientIds,
                advisorId,
                status,
                paymentStatus,
                filters.ConvertedFrom,
                filters.ConvertedTo,
                filters.SaleNumber,
                after,
                limit,
                ct),
            async (batch, ct) =>
            {
                var rows = await OrderListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, ct);
                return rows.Select(ToCells);
            },
            row => new OrderExportCursor(row.Order.ConvertedAt, row.Order.OrderNumber),
            cancellationToken);

        if (rowCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no sales matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
        var upload = await storage.UploadAsync(
            job.TenantId, job.Id, fileName, workbook.Complete(), cancellationToken);
        return new ExportJobResult(fileName, rowCount, upload.DownloadUrl, upload.ExpiresAt);
    }

    // El request ya validó el estado y la forma de pago; si igual no se pueden leer —el enum
    // cambió entre el pedido y el proceso—, reintentar no lo arregla.
    private static (OrderStatus? Status, OrderPaymentStatus? PaymentStatus) ParseStatuses(OrdersExportFilters filters)
    {
        try
        {
            return (OrderListing.ParseStatus(filters.Status), OrderListing.ParsePaymentStatus(filters.PaymentStatus));
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private static ExportCell[] ToCells(OrderListItemDto row) =>
    [
        ExportCell.OfText(row.OrderNumber),
        ExportCell.OfText(row.ClientName),
        ExportCell.OfText(row.AdvisorEmail),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, mismo criterio que cotizaciones.
        ExportCell.OfText(row.ConvertedAt.ToString("O", CultureInfo.InvariantCulture)),
        // El DTO trae los nombres de los enums, que son contrato de la API (OrderMapping.cs);
        // acá se vuelven al enum sólo para etiquetarlos.
        ExportCell.OfText(row.PaymentMethod
            ?? ExportStatusLabels.For(Enum.Parse<OrderPaymentStatus>(row.PaymentStatus))),
        ExportCell.OfText(ExportStatusLabels.For(Enum.Parse<OrderStatus>(row.Status))),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
    ];
}
