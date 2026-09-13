using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de ventas en el worker (D7, D8). Mismo esquema que
/// <see cref="QuotationsExportProcessor"/>: lotes de mil con keyset y el filtro del listado
/// (<see cref="SaleListing"/> y el <c>Filtered</c> de SaleRepository), streaming y subida con el id
/// del job. El lote de lectura y escritura lo comparten los dos en <see cref="ExportBatchLoop"/>.
/// </summary>
public sealed class SalesExportProcessor(
    ISaleRepository repository,
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
    /// Las de la tabla de ventas en su orden (sale-table.tsx: Venta, Cliente, Asesora, Fecha, Pago,
    /// Estado, Total), con la moneda aparte del total y "Asesor" como en el Excel de cotizaciones.
    /// "Pago" replica el respaldo de la tabla: la forma de pago, o el estado del pago mientras la
    /// forma llegue vacía.
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
        var filters = ExportJobFilters.Read<SalesExportFilters>(job);
        var (status, paymentStatus) = ParseStatuses(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await SaleListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        var generatedAt = clock.UtcNow;

        using var workbook = writer.Create(SheetName, Columns);
        var rowCount = await ExportBatchLoop.WriteAllAsync<SaleWithQuotation, SaleExportCursor>(
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
                var rows = await SaleListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, ct);
                return rows.Select(ToCells);
            },
            row => new SaleExportCursor(row.Sale.ConvertedAt, row.Sale.SaleNumber),
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
    private static (SaleStatus? Status, SalePaymentStatus? PaymentStatus) ParseStatuses(SalesExportFilters filters)
    {
        try
        {
            return (SaleListing.ParseStatus(filters.Status), SaleListing.ParsePaymentStatus(filters.PaymentStatus));
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private static ExportCell[] ToCells(SaleListItemDto row) =>
    [
        ExportCell.OfText(row.SaleNumber),
        ExportCell.OfText(row.ClientName),
        ExportCell.OfText(row.AdvisorEmail),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, mismo criterio que cotizaciones.
        ExportCell.OfText(row.ConvertedAt.ToString("O", CultureInfo.InvariantCulture)),
        ExportCell.OfText(row.PaymentMethod ?? row.PaymentStatus),
        ExportCell.OfText(row.Status),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
    ];
}
