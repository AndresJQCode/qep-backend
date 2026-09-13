using System.Globalization;
using BuildingBlocks.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de cotizaciones en el worker (D7, D8). Lee por lotes de mil con keyset
/// y el mismo filtro que la tabla —<c>FilteredQuery</c> y <see cref="QuotationListing"/>—, escribe
/// cada lote en streaming y sube el archivo con el id del job.
/// </summary>
public sealed class QuotationsExportProcessor(
    IQuotationRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    IClock clock)
    : IExportJobProcessor
{
    public const string SheetName = "Cotizaciones";

    public const string FilePrefix = "cotizaciones";

    /// <summary>Las de la tabla del listado, en su orden (D8), con la moneda aparte del total
    /// —la tabla la pinta junto al importe y en una planilla tiene que poder filtrarse—. Sin
    /// tildes, como Reporting. Anchos fijos: medir el contenido obligaría a recorrerlo dos veces;
    /// Fecha cabe entera en formato ISO.</summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("Numero", 16),
        new("Fecha", 34),
        new("Cliente", 40),
        new("Asesor", 32),
        new("Estado", 12),
        new("Moneda", 10),
        new("Total", 16),
    ];

    public ExportJobKind Kind => ExportJobKind.Quotations;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<QuotationsExportFilters>(job);
        var status = ParseStatus(filters.Status);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        // El NIT se resuelve al generar, no al pedir: el archivo refleja los clientes de ahora.
        var clientIds = await QuotationListing.ResolveClientIdsByNitAsync(
            customerLookup, job.TenantId, filters.ClientNit, cancellationToken);
        var generatedAt = clock.UtcNow;

        using var workbook = writer.Create(SheetName, Columns);
        var rowCount = 0;
        QuotationExportCursor? after = null;
        while (true)
        {
            // Keyset (D8): el lote siguiente arranca después de la última fila leída, no en un
            // offset que una cotización nueva o anulada durante el export correría.
            var batch = await repository.ListForExportAsync(
                job.TenantId,
                filters.ClientId,
                clientIds,
                advisorId,
                status,
                filters.CreatedFrom,
                filters.CreatedTo,
                filters.QuotationNumber,
                after,
                ExportJobLimits.BatchSize,
                cancellationToken);

            if (batch.Count > 0)
            {
                // Nombres y correos por lote: dos idas por cada mil filas, no una por fila.
                var rows = await QuotationListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, cancellationToken);
                foreach (var row in rows)
                {
                    workbook.AppendRow(ToCells(row));
                }

                after = new QuotationExportCursor(batch[^1].CreatedAt, batch[^1].QuotationNumber);
            }

            rowCount += batch.Count;
            if (batch.Count < ExportJobLimits.BatchSize)
            {
                break;
            }
        }

        // Había filas cuando se pidió (el EXISTS del request) y ya no: reintentar da lo mismo.
        if (rowCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no quotations matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, generatedAt);
        var upload = await storage.UploadAsync(
            job.TenantId, job.Id, fileName, workbook.Complete(), cancellationToken);
        return new ExportJobResult(fileName, rowCount, upload.DownloadUrl, upload.ExpiresAt);
    }

    // El request ya validó el estado; si igual no se puede leer —el enum cambió entre el pedido y
    // el proceso—, reintentar no lo arregla.
    private static QuotationStatus? ParseStatus(string? status)
    {
        try
        {
            return QuotationListing.ParseStatus(status);
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private static ExportCell[] ToCells(QuotationListItemDto row) =>
    [
        ExportCell.OfText(row.QuotationNumber),
        // Texto ISO y no celda de fecha: una fecha se muestra según la configuración regional de
        // quien abre el archivo, y ahí 03/04 deja de ser una fecha sola.
        ExportCell.OfText(row.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
        ExportCell.OfText(row.ClientName),
        ExportCell.OfText(row.AdvisorEmail),
        ExportCell.OfText(row.Status),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
    ];
}
