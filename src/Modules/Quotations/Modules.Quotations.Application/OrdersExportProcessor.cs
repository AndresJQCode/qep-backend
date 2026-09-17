using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel del listado de pedidos en el worker (D7, D8). Mismo esquema que
/// <see cref="QuotationsExportProcessor"/>: lotes de mil con keyset y el filtro del listado
/// (<see cref="OrderListing"/> y el <c>Filtered</c> de OrderRepository), streaming y subida con el id
/// del job. El lote de lectura y escritura lo comparten los dos en <see cref="ExportBatchLoop"/>.
///
/// Suma los comprobantes de pago de cada pedido (spec 2026-09-15): se leen con una consulta por lote
/// (E6), y cada uno es el enlace a su copia pública, «Sin enlace» si es privado, o una celda vacía
/// si el pedido no lo tiene (E2).
/// </summary>
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    ITenantClock tenantClock)
    : IExportJobProcessor
{
    public const string SheetName = "Pedidos";

    public const string FilePrefix = "pedidos";

    /// <summary>El texto de la celda de un comprobante con copia pública (E2).</summary>
    public const string ProofLinkText = "Ver";

    /// <summary>La celda de un comprobante privado (E2): que no haya enlace no es lo mismo que no
    /// haya comprobante, y una celda vacía no puede significar las dos cosas.</summary>
    public const string PrivateProofText = "Sin enlace";

    /// <summary>
    /// Las de la tabla de pedidos en su orden (order-table.tsx: Pedido, Cliente, Asesora, Fecha, Pago,
    /// Estado, Total), con la moneda aparte del total y "Asesor" como en el Excel de cotizaciones.
    /// "Pago" replica el respaldo de la tabla: la forma de pago o, mientras llegue vacía, la etiqueta del
    /// estado del pago. Los estados van con la etiqueta de la pantalla (spec 2026-09-13, A7).
    ///
    /// Después de Total, las cuatro de los comprobantes (spec 2026-09-15, E1): la cantidad y los tres
    /// primeros. Van al final para que las ocho de la tabla no se muevan, y salen siempre, aunque la
    /// opción de publicar esté apagada (E7): la forma del archivo no depende del ambiente.
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("Pedido", 18),
        new("Cliente", 40),
        new("Asesor", 32),
        new("Fecha", 34),
        new("Pago", 24),
        new("Estado", 12),
        new("Moneda", 10),
        new("Total", 16),
        new("Comprobantes", 14),
        new("Comprobante 1", 16),
        new("Comprobante 2", 16),
        new("Comprobante 3", 16),
    ];

    public ExportJobKind Kind => ExportJobKind.Orders;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<OrdersExportFilters>(job);
        var (status, paymentStatus) = ParseStatuses(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        // Un calendario por job (spec 2026-09-17): corta el rango guardado en el día del tenant.
        var calendar = await tenantClock.GetAsync(job.TenantId, cancellationToken);
        var converted = TenantDayRange.Of(calendar, filters.ConvertedFrom, filters.ConvertedTo);
        var generatedAt = calendar.UtcNow;

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
                converted.From,
                converted.Before,
                filters.OrderNumber,
                after,
                limit,
                ct),
            async (batch, ct) =>
            {
                var rows = await OrderListing.ToListItemsAsync(
                    customerLookup, advisorLookup, job.TenantId, batch, ct);
                // E6: los comprobantes del lote en una sola ida, igual que los nombres y los correos.
                var proofs = await repository.ListPaymentProofsForExportAsync(
                    job.TenantId, batch.Select(row => row.Order.Id).ToArray(), ct);
                return rows.Select(row => ToCells(row, ProofsOf(proofs, row.Id), calendar));
            },
            row => new OrderExportCursor(row.Order.ConvertedAt, row.Order.OrderNumber),
            cancellationToken);

        if (rowCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no orders matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, calendar.ToLocal(generatedAt));
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

    private static IReadOnlyList<OrderExportPaymentProof> ProofsOf(
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>> proofs, Guid orderId) =>
        proofs.TryGetValue(new OrderId(orderId), out var found) ? found : [];

    private ExportCell[] ToCells(
        OrderListItemDto row, IReadOnlyList<OrderExportPaymentProof> proofs, TenantCalendar calendar) =>
    [
        ExportCell.OfText(row.OrderNumber),
        ExportCell.OfText(row.ClientName),
        // El nombre con respaldo al correo, igual que la tabla (spec 2026-09-11, D1, nota del
        // 2026-09-15). El encabezado sigue siendo "Asesor", como en el Excel de cotizaciones.
        ExportCell.OfText(row.AdvisorName),
        // Texto y no celda de fecha, mismo criterio que cotizaciones, en la hora del tenant, al minuto
        // y sin offset (spec 2026-09-17, punto 8a).
        ExportCell.OfText(calendar.ToLocal(row.ConvertedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        // El DTO trae los nombres de los enums, que son contrato de la API (OrderMapping.cs);
        // acá se vuelven al enum sólo para etiquetarlos.
        ExportCell.OfText(row.PaymentMethod
            ?? ExportStatusLabels.For(Enum.Parse<OrderPaymentStatus>(row.PaymentStatus))),
        ExportCell.OfText(ExportStatusLabels.For(Enum.Parse<OrderStatus>(row.Status))),
        ExportCell.OfText(row.Currency),
        ExportCell.OfNumber(row.Total),
        // La cantidad cuenta todos, también los que no tienen columna (E1).
        ExportCell.OfNumber(proofs.Count),
        ProofCell(proofs, 0),
        ProofCell(proofs, 1),
        ProofCell(proofs, 2),
    ];

    // E2: el enlace «Ver» si tiene copia pública y la opción está encendida, «Sin enlace» si no, y
    // vacía si el pedido no tiene ese comprobante.
    private ExportCell ProofCell(IReadOnlyList<OrderExportPaymentProof> proofs, int index)
    {
        if (index >= proofs.Count)
        {
            return ExportCell.OfText(string.Empty);
        }

        var url = proofs[index].PublicStorageKey is { } publicKey
            ? paymentProofPublisher.UrlFor(publicKey)
            : null;
        return url is null ? ExportCell.OfText(PrivateProofText) : ExportCell.OfLink(url, ProofLinkText);
    }
}
