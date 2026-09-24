using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma el Excel de pedidos para el ERP contable del tenant (ajuste 2026-09-20): una fila por
/// línea de producto, no por pedido — el ERP necesita Cod. Producto, Cantidad, Valor Unit, IVA y
/// Descuento por línea, y esos datos no existen a nivel pedido. Los campos que sí son del pedido
/// entero (EMPRESA, Pedido, Documento, dirección de entrega, Observaciones) se repiten en cada una
/// de sus líneas.
///
/// Mismo esquema de lote que <see cref="QuotationsExportProcessor"/>: keyset de a mil y streaming
/// (<see cref="ExportBatchLoop"/>). Lo que cambia es que <c>toRows</c> ya no devuelve una fila por
/// entidad del lote — un pedido con tres líneas aporta tres filas — así que el conteo de filas del
/// archivo se lleva aparte del conteo de pedidos leídos (ver <see cref="ProcessAsync"/>).
/// </summary>
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IQuotationCustomerLookup customerLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationGeographyLookup geographyLookup,
    IQuotationAdvisorLookup advisorLookup,
    IPaymentProofPublisher paymentProofPublisher,
    IExportWorkbookWriter writer,
    IExportFileStorage storage,
    ITenantClock tenantClock)
    : IExportJobProcessor
{
    public const string SheetName = "Pedidos";

    public const string FilePrefix = "pedidos";

    /// <summary>Cuántas fechas de pago tienen columna propia, mismo criterio y mismo número que
    /// <c>OrderPaymentProof</c> antes de este ajuste: es lo que paga un pedido en la práctica.
    /// </summary>
    public const int PaymentDateColumns = 5;

    /// <summary>La celda "URL Comprobante N" de un comprobante privado, o con los enlaces públicos
    /// apagados: que no haya enlace no es lo mismo que no haya comprobante, y una celda vacía no
    /// puede significar las dos cosas.</summary>
    public const string PrivateProofText = "Sin enlace";

    /// <summary>
    /// Las columnas que el ERP contable espera, en su orden (ajuste 2026-09-20). "Nota Detalle"
    /// sale siempre vacía: no existe una nota por línea, sólo <c>Quotation.Notes</c> a nivel
    /// pedido, que ya es la columna "Observaciones". "Vencimiento" no está: no hay lote ni
    /// caducidad de producto en ningún lado del sistema todavía.
    ///
    /// "Cod. Asesor" (spec 2026-09-24, D9) va al final a propósito: el ERP lee por encabezado, y
    /// al final no mueve nada de lo que ya importa. Es el nombre por defecto que la homologación de
    /// columnas por tenant (D10, otro spec) podrá renombrar.
    ///
    /// Después van "Banco" y "Cuenta" (2026-09-24), una vez por fila: la cuenta de facturación
    /// congelada en la cotización, la misma para todos los comprobantes del pedido. Y por cada
    /// comprobante, en el orden de "Fecha Pago N", "V. Comprobante N" con su monto y "URL
    /// Comprobante N" con el enlace clicable a su copia pública —la URL misma como texto, para que
    /// se lea sin abrirla—, o «Sin enlace» si es privado. También al final, por la misma razón que
    /// "Cod. Asesor".
    ///
    /// Y de última, "Valor Unit sin IVA" (2026-09-24): el precio unitario con el IVA que trae
    /// adentro quitado (<see cref="QuotationItem.UnitPriceWithoutTax"/>). Al final por la misma
    /// razón que las anteriores. "Valor Unit" no cambia y sigue llevando el precio con IVA incluido,
    /// que es como se carga <see cref="QuotationItem.UnitPrice"/>.
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn> Columns =
    [
        new("EMPRESA", 30),
        new("Cod. Producto", 18),
        new("Cantidad", 12),
        new("Valor Unit", 16),
        new("IVA", 14),
        new("Descuento", 14),
        new("Nota Detalle", 30),
        .. Enumerable.Range(1, PaymentDateColumns).Select(number => new ExportColumn($"Fecha Pago {number}", 18)),
        new("Ciudad", 20),
        new("Documento", 16),
        new("Pedido", 18),
        new("Direccion", 40),
        new("Observaciones", 40),
        new("Telefono", 16),
        new("Email", 30),
        new("Cod. Asesor", 14),
        new("Banco", 24),
        new("Cuenta", 20),
        .. Enumerable.Range(1, PaymentDateColumns).SelectMany(number => new ExportColumn[]
        {
            new($"V. Comprobante {number}", 18),
            new($"URL Comprobante {number}", 60),
        }),
        new("Valor Unit sin IVA", 18),
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

        // El conteo del archivo (filas de producto) se lleva aparte del que devuelve el lote
        // (pedidos leídos, para el chequeo de "vacío" de abajo): un pedido con tres líneas aporta
        // tres filas, y ExportBatchLoop no lo sabe — cuenta entidades del lote, no lo que
        // devuelve `toRows`.
        var exportedRows = 0;

        using var workbook = writer.Create(SheetName, Columns);
        var orderCount = await ExportBatchLoop.WriteAllAsync<OrderWithQuotation, OrderExportCursor>(
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
                var context = await LoadBatchContextAsync(job.TenantId, batch, ct);
                var rows = batch.SelectMany(row => RowsFor(row, context, calendar, paymentProofPublisher)).ToArray();
                exportedRows += rows.Length;
                return rows;
            },
            row => new OrderExportCursor(row.Order.ConvertedAt, row.Order.OrderNumber),
            cancellationToken);

        if (orderCount == 0)
        {
            throw new ExportJobDefinitiveException(
                "Empty: no orders matched the export filters when the export ran.");
        }

        var fileName = ExportFileNames.For(FilePrefix, calendar.ToLocal(generatedAt));
        var upload = await storage.UploadAsync(
            job.TenantId, job.Id, fileName, workbook.Complete(), cancellationToken);
        return new ExportJobResult(fileName, exportedRows, upload.DownloadUrl, upload.ExpiresAt);
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

    /// <summary>Todo lo que un lote necesita resuelto de una sola vez, para que <see cref="RowsFor"/>
    /// no pida nada por fila: líneas y partes por cotización, comprobantes por pedido, productos y
    /// empresas por id, ciudades de las partes de entrega, la ficha de cada cliente del lote —
    /// la necesitan tanto "Documento" (siempre) como el respaldo de "los mismos datos del cliente"
    /// cuando el pedido no tiene una parte de entrega propia— y la asesora de cada cotización, por
    /// su código.</summary>
    private readonly record struct BatchContext(
        IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationItem>> ItemsByQuotation,
        IReadOnlyDictionary<QuotationId, IReadOnlyList<QuotationParty>> PartiesByQuotation,
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>> ProofsByOrder,
        IReadOnlyDictionary<Guid, QuotationProductRef> Products,
        IReadOnlyDictionary<Guid, QuotationCompanyRef> Companies,
        IReadOnlyDictionary<Guid, string> CityNames,
        IReadOnlyDictionary<Guid, QuotationCustomerRef> Customers,
        IReadOnlyDictionary<Guid, QuotationAdvisor> Advisors);

    private async Task<BatchContext> LoadBatchContextAsync(
        Guid tenantId, IReadOnlyList<OrderWithQuotation> batch, CancellationToken cancellationToken)
    {
        var quotationIds = batch.Select(row => row.Quotation.Id).ToArray();
        var orderIds = batch.Select(row => row.Order.Id).ToArray();

        var itemsByQuotation = await repository.ListItemsForExportAsync(tenantId, quotationIds, cancellationToken);
        var partiesByQuotation = await repository.ListPartiesForExportAsync(tenantId, quotationIds, cancellationToken);
        var proofsByOrder = await repository.ListPaymentProofsForExportAsync(tenantId, orderIds, cancellationToken);

        var productIds = itemsByQuotation.Values
            .SelectMany(items => items)
            .Select(item => item.ProductId)
            .Distinct()
            .ToArray();
        var products = await productLookup.FindManyAsync(tenantId, productIds, cancellationToken);

        // Pocas empresas distintas en la práctica (un tenant no suele facturar por más de un
        // puñado de cuentas), así que una consulta por empresa distinta y no un puerto batch
        // nuevo — a diferencia de clientes, donde "los mismos datos del cliente" es el caso
        // normal y el batch sí importa.
        var companyIds = batch
            .Select(row => row.Quotation.BillingAccount?.CompanyId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var companies = new Dictionary<Guid, QuotationCompanyRef>();
        foreach (var companyId in companyIds)
        {
            var company = await companyLookup.FindAsync(tenantId, companyId, cancellationToken);
            if (company is not null)
            {
                companies[companyId] = company;
            }
        }

        var shippingCityIds = partiesByQuotation.Values
            .SelectMany(parties => parties)
            .Where(party => party.Role == QuotationPartyRole.Shipping && party.CityId is not null)
            .Select(party => party.CityId!.Value)
            .Distinct()
            .ToArray();
        var cityNames = await geographyLookup.FindCityNamesAsync(shippingCityIds, cancellationToken);

        // Todos los clientes del lote, no sólo los que necesitan el respaldo: "Documento" los
        // necesita a todos.
        var clientIds = batch.Select(row => row.Quotation.ClientId).Distinct().ToArray();
        var customers = await customerLookup.FindManyAsync(tenantId, clientIds, cancellationToken);

        // "Cod. Asesor" (D9): Quotation.AdvisorId → membresía → código de hoy, en una consulta
        // por lote con las asesoras distintas, que en un lote son una o dos.
        var advisorIds = batch.Select(row => row.Quotation.AdvisorId.Value).Distinct().ToArray();
        var advisors = await advisorLookup.FindAsync(tenantId, advisorIds, cancellationToken);

        return new BatchContext(
            itemsByQuotation, partiesByQuotation, proofsByOrder, products, companies, cityNames, customers,
            advisors);
    }

    private static IEnumerable<ExportCell[]> RowsFor(
        OrderWithQuotation row, BatchContext context, TenantCalendar calendar, IPaymentProofPublisher publisher)
    {
        var quotation = row.Quotation;
        var order = row.Order;

        var items = context.ItemsByQuotation.TryGetValue(quotation.Id, out var foundItems)
            ? foundItems
            : [];
        if (items.Count == 0)
        {
            yield break;
        }

        var proofs = context.ProofsByOrder.TryGetValue(order.Id, out var foundProofs)
            ? foundProofs
            : [];

        var empresa = quotation.BillingAccount is { } billing
            && context.Companies.TryGetValue(billing.CompanyId, out var company)
                ? company.Name
                : string.Empty;

        context.Customers.TryGetValue(quotation.ClientId, out var customer);
        var documento = customer?.Cuc ?? string.Empty;

        var shipping = context.PartiesByQuotation.TryGetValue(quotation.Id, out var parties)
            ? parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Shipping)
            : null;
        var (ciudad, direccion, telefono, email) = ContactFor(shipping, customer, context.CityNames);

        var observaciones = quotation.Notes ?? string.Empty;
        var codAsesor = AdvisorCodeCell(quotation, context.Advisors);
        var banco = quotation.BillingAccount?.BankName ?? string.Empty;
        var cuenta = quotation.BillingAccount?.AccountNumber ?? string.Empty;
        // Iguales en todas las líneas del pedido: se arman una vez, no por línea.
        var proofCells = Enumerable.Range(0, PaymentDateColumns)
            .SelectMany(index => ProofCells(proofs, index, publisher))
            .ToArray();

        foreach (var item in items)
        {
            context.Products.TryGetValue(item.ProductId, out var product);

            yield return
            [
                ExportCell.OfText(empresa),
                ExportCell.OfText(product?.Code ?? string.Empty),
                ExportCell.OfNumber(item.Quantity),
                ExportCell.OfNumber(item.UnitPrice),
                ExportCell.OfNumber(item.TaxAmount),
                ExportCell.OfNumber(item.DiscountAmount),
                ExportCell.OfText(string.Empty), // Nota Detalle: no existe nota por línea.
                .. Enumerable.Range(0, PaymentDateColumns).Select(index => PaymentDateCell(proofs, index, calendar)),
                ExportCell.OfText(ciudad),
                ExportCell.OfText(documento),
                ExportCell.OfText(order.OrderNumber),
                ExportCell.OfText(direccion),
                ExportCell.OfText(observaciones),
                ExportCell.OfText(telefono),
                ExportCell.OfText(email),
                codAsesor,
                ExportCell.OfText(banco),
                ExportCell.OfText(cuenta),
                .. proofCells,
                ExportCell.OfNumber(item.UnitPriceWithoutTax),
            ];
        }
    }

    // La entrega con datos propios manda; sin ella, "los mismos datos del cliente" (el caso
    // normal, ver QuotationParty) resuelve contra la ficha del cliente ya cargada en el contexto.
    private static (string Ciudad, string Direccion, string Telefono, string Email) ContactFor(
        QuotationParty? shipping,
        QuotationCustomerRef? customer,
        IReadOnlyDictionary<Guid, string> cityNames)
    {
        if (shipping is not null)
        {
            var ciudad = shipping.CityId is { } cityId && cityNames.TryGetValue(cityId, out var name)
                ? name
                : string.Empty;
            return (ciudad, shipping.Address ?? string.Empty, shipping.Phone ?? string.Empty, shipping.Email ?? string.Empty);
        }

        return (
            customer?.CityName ?? string.Empty,
            customer?.Address ?? string.Empty,
            customer?.Phone ?? string.Empty,
            customer?.Email ?? string.Empty);
    }

    // "Cod. Asesor" (D9): numérica, para que el ERP la lea como el número que es. Vacía si la
    // membresía no tiene código o si el lookup no la devuelve —otro tenant, una fila que ya no
    // está—: un pedido sin código no es una razón para frenar el archivo.
    private static ExportCell AdvisorCodeCell(
        Quotation quotation, IReadOnlyDictionary<Guid, QuotationAdvisor> advisors) =>
        advisors.TryGetValue(quotation.AdvisorId.Value, out var advisor)
            && advisor.AdvisorCode is { } code
                ? ExportCell.OfNumber(code)
                : ExportCell.OfText(string.Empty);

    private static ExportCell PaymentDateCell(
        IReadOnlyList<OrderExportPaymentProof> proofs, int index, TenantCalendar calendar) =>
        index >= proofs.Count
            ? ExportCell.OfText(string.Empty)
            : ExportCell.OfText(calendar.ToLocal(proofs[index].UploadedAt)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    // "V. Comprobante N" y "URL Comprobante N" (2026-09-24): el monto como número, porque el ERP lo
    // suma, y el enlace con la URL como texto. Sin copia pública, o con la opción apagada —UrlFor
    // da null—, «Sin enlace»; sin comprobante en ese índice, las dos celdas vacías.
    private static ExportCell[] ProofCells(
        IReadOnlyList<OrderExportPaymentProof> proofs, int index, IPaymentProofPublisher publisher)
    {
        if (index >= proofs.Count)
        {
            return [ExportCell.OfText(string.Empty), ExportCell.OfText(string.Empty)];
        }

        var proof = proofs[index];
        var url = proof.PublicStorageKey is { } publicKey ? publisher.UrlFor(publicKey) : null;
        return
        [
            ExportCell.OfNumber(proof.Amount),
            url is null ? ExportCell.OfText(PrivateProofText) : ExportCell.OfLink(url, url),
        ];
    }
}
