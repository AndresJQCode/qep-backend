using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// El Excel de pedidos (spec 2026-09-12 y homologación por tenant). Desde el spec 2026-10-15 §6.4 las
/// columnas del catálogo (<see cref="OrdersExportColumnCatalog.Columns"/>) conservan clave, encabezado y
/// orden, pero todo sale del pedido: su cuenta de cobro, sus líneas, sus partes, su cliente, su asesor y
/// sus totales. Un pedido sin cotización exporta igual.
/// </summary>
public sealed class OrdersExportProcessor(
    IOrderRepository repository,
    IOrdersExportLayoutRepository layoutRepository,
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
    public const string FilePrefix = "pedidos";

    public const int PaymentDateColumns = OrdersExportColumnCatalog.PaymentDateColumns;

    /// <summary>Texto de la celda de enlace cuando el comprobante no tiene copia pública.</summary>
    public const string PrivateProofText = "Sin enlace";

    public const string StorePickupCarrier = "Recoger en tienda";

    public const string DefaultCarrier = "Coordinadora";

    public static readonly IReadOnlyList<ExportColumn> Columns = OrdersExportColumnCatalog.Columns
        .Where(column => column.DefaultVisible)
        .Select(column => new ExportColumn(column.DefaultHeader, column.Width))
        .ToArray();

    public ExportJobKind Kind => ExportJobKind.Orders;

    public async Task<ExportJobResult> ProcessAsync(ExportJob job, CancellationToken cancellationToken)
    {
        var filters = ExportJobFilters.Read<OrdersExportFilters>(job);
        var (status, paymentStatus, source) = ParseFilters(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        // Un calendario por job (spec 2026-09-17). El rango se corta en la zona de quien lo pidió, la
        // misma con que vio la tabla; las fechas del archivo siguen en el día del tenant, porque las
        // importa su ERP.
        var calendar = await tenantClock.GetAsync(job.TenantId, cancellationToken);
        var converted = TenantDayRange.Of(
            OrderListing.CalendarFor(calendar, filters.TimeZone), filters.ConvertedFrom, filters.ConvertedTo);
        var generatedAt = calendar.UtcNow;

        var storedLayout = await layoutRepository.FindAsync(job.TenantId, cancellationToken);
        var layout = OrdersExportLayoutProjection.For(OrdersExportLayout.Effective(storedLayout));
        var sheetName = OrdersExportLayout.EffectiveSheetName(storedLayout);

        var exportedRows = 0;
        using var workbook = writer.Create(sheetName, layout.Columns);
        var orderCount = await ExportBatchLoop.WriteAllAsync<Order, OrderExportCursor>(
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
                source,
                after,
                limit,
                ct),
            async (batch, ct) =>
            {
                var context = await LoadBatchContextAsync(job.TenantId, batch, ct);
                var rows = batch
                    .SelectMany(order => RowsFor(order, context, calendar, paymentProofPublisher))
                    .Select(cells => layout.Project(cells))
                    .ToArray();
                exportedRows += rows.Length;
                return rows;
            },
            order => new OrderExportCursor(order.ConvertedAt, order.OrderNumber),
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

    // Un filtro ilegible es definitivo: el trabajo nació con él y reintentar no lo arregla.
    private static (OrderStatus? Status, OrderPaymentStatus? PaymentStatus, OrderSource? Source) ParseFilters(
        OrdersExportFilters filters)
    {
        try
        {
            return (
                OrderListing.ParseStatus(filters.Status),
                OrderListing.ParsePaymentStatus(filters.PaymentStatus),
                OrderListing.ParseSource(filters.Source));
        }
        catch (QuotationsDomainException exception)
        {
            throw new ExportJobDefinitiveException($"UnreadableFilters: {exception.Message}", exception);
        }
    }

    private readonly record struct BatchContext(
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderItem>> ItemsByOrder,
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderParty>> PartiesByOrder,
        IReadOnlyDictionary<OrderId, IReadOnlyList<OrderExportPaymentProof>> ProofsByOrder,
        IReadOnlyDictionary<Guid, QuotationVariantRef> Products,
        IReadOnlyDictionary<Guid, QuotationCompanyRef> Companies,
        IReadOnlyDictionary<Guid, string> CityNames,
        IReadOnlyDictionary<Guid, QuotationCustomerRef> Customers,
        IReadOnlyDictionary<Guid, QuotationAdvisor> Advisors,
        IReadOnlyDictionary<Guid, string> CoordinadoraCityNames);

    // Todo lo que un lote necesita, en una consulta por tipo (spec 2026-09-15, E6): nunca una por
    // pedido.
    private async Task<BatchContext> LoadBatchContextAsync(
        Guid tenantId, IReadOnlyList<Order> batch, CancellationToken cancellationToken)
    {
        var orderIds = batch.Select(order => order.Id).ToArray();
        var itemsByOrder = await repository.ListItemsForExportAsync(tenantId, orderIds, cancellationToken);
        var partiesByOrder = await repository.ListPartiesForExportAsync(tenantId, orderIds, cancellationToken);
        var proofsByOrder = await repository.ListPaymentProofsForExportAsync(tenantId, orderIds, cancellationToken);

        // Sólo las líneas sin foto consultan el catálogo: la foto es lo que el pedido vendió.
        var variantIds = itemsByOrder.Values
            .SelectMany(items => items)
            .Where(item => item.ProductCode is null)
            .Select(item => item.VariantId)
            .Distinct()
            .ToArray();
        var products = variantIds.Length == 0
            ? new Dictionary<Guid, QuotationVariantRef>()
            : await productLookup.FindManyAsync(tenantId, variantIds, cancellationToken);

        // Un pedido guardado siempre tiene cuenta de cobro: sólo el cálculo previo nace sin ella, y no se
        // guarda (Order.Preview). De ahí el `!` acá y en RowsFor.
        var companyIds = batch.Select(order => order.BillingAccount!.CompanyId).Distinct().ToArray();
        var companies = new Dictionary<Guid, QuotationCompanyRef>();
        foreach (var companyId in companyIds)
        {
            var company = await companyLookup.FindAsync(tenantId, companyId, cancellationToken);
            if (company is not null)
            {
                companies[companyId] = company;
            }
        }

        var shippingCityIds = partiesByOrder.Values
            .SelectMany(parties => parties)
            .Where(party => party.Role == QuotationPartyRole.Shipping && party.CityId is not null)
            .Select(party => party.CityId!.Value)
            .Distinct()
            .ToArray();
        var cityNames = await geographyLookup.FindCityNamesAsync(shippingCityIds, cancellationToken);

        var clientIds = batch.Select(order => order.ClientId).Distinct().ToArray();
        var customers = await customerLookup.FindManyAsync(tenantId, clientIds, cancellationToken);

        var coordinadoraCityIds = shippingCityIds
            .Concat(customers.Values.Where(customer => customer.CityId is not null).Select(customer => customer.CityId!.Value))
            .Distinct()
            .ToArray();
        var coordinadoraCityNames = await geographyLookup.FindCoordinadoraCityNamesAsync(
            coordinadoraCityIds, cancellationToken);

        var advisorIds = batch.Select(order => order.AdvisorId.Value).Distinct().ToArray();
        var advisors = await advisorLookup.FindAsync(tenantId, advisorIds, cancellationToken);

        return new BatchContext(
            itemsByOrder, partiesByOrder, proofsByOrder, products, companies, cityNames, customers,
            advisors, coordinadoraCityNames);
    }

    // Una fila por línea del pedido. Un pedido sin líneas no puede existir (el dominio lo exige),
    // pero el procesador se defiende igual.
    private static IEnumerable<ExportCell[]> RowsFor(
        Order order, BatchContext context, TenantCalendar calendar, IPaymentProofPublisher publisher)
    {
        var items = context.ItemsByOrder.TryGetValue(order.Id, out var foundItems)
            ? foundItems
            : [];
        if (items.Count == 0)
        {
            yield break;
        }

        var proofs = context.ProofsByOrder.TryGetValue(order.Id, out var foundProofs)
            ? foundProofs
            : [];
        var billingCompany = context.Companies.TryGetValue(order.BillingAccount!.CompanyId, out var company)
            ? company
            : null;
        var empresa = billingCompany?.Name ?? string.Empty;
        var nitEmpresa = OrdersExportLayoutProjection.FixedCellFor(ErpTaxId(billingCompany?.TaxId));
        context.Customers.TryGetValue(order.ClientId, out var customer);
        var documento = customer?.Cuc ?? string.Empty;
        var parties = context.PartiesByOrder.TryGetValue(order.Id, out var foundParties)
            ? foundParties
            : [];
        var shipping = parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Shipping);
        var billingParty = parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Billing);
        var (ciudad, direccion, telefono, email) = ContactFor(shipping, customer, context.CityNames);
        var ciudadCoordinadora = CoordinadoraCityFor(shipping, customer, context.CoordinadoraCityNames);
        var cliente = BilledNameFor(order, billingParty, customer);
        var documentoIdentidad = BilledIdentificationFor(order, billingParty, customer);
        // D-R8 (spec 2026-10-23): ISO fijo a propósito. Esta hoja es el contrato con el importador del
        // ERP, no un documento para una persona: no sigue el formato de fecha del tenant. No lo «arregles».
        var fechaPedido = calendar.ToLocal(order.CreatedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Spec 2026-10-15 §6.4 (y el hotfix del mismo día): las observaciones son las del pedido, que
        // al convertir heredó las de la cotización.
        var observaciones = order.Notes ?? string.Empty;
        var codAsesor = AdvisorCodeCell(order, context.Advisors);
        // "Transportadora" (spec 2026-10-05): sólo depende de la recogida. Las demás columnas de
        // entrega no cambian con ella (decisión del owner): sin parte de envío caen al cliente.
        // Spec 2026-10-17 flete (D-F8): la transportadora real del pedido; sin ella, "Coordinadora".
        var transportadora = order.IsStorePickup
            ? StorePickupCarrier
            : string.IsNullOrWhiteSpace(order.CarrierName) ? DefaultCarrier : order.CarrierName;
        // Flete: con recogida o sin modo no hay valor ni modo. Al cobro deja vacío "Valor Flete" porque
        // el monto se paga a la transportadora y no es del documento; los desgloses salen en 0.
        var freightMode = order.IsStorePickup ? null : order.FreightChargeMode;
        var fleteValor = freightMode switch
        {
            FreightChargeMode.Charged => ExportCell.OfNumber(order.FreightAmount),
            FreightChargeMode.Free => ExportCell.OfNumber(0m),
            _ => ExportCell.OfText(string.Empty),
        };
        var fleteSinIva = ExportCell.OfNumber(order.FreightSubtotal);
        var fleteIva = ExportCell.OfNumber(order.FreightTaxAmount);
        var fleteModo = ExportCell.OfText(freightMode switch
        {
            FreightChargeMode.Charged => "Cobrado",
            FreightChargeMode.Free => "Gratis",
            FreightChargeMode.OnDelivery => "Al cobro",
            _ => string.Empty,
        });
        var contraentrega = ExportCell.OfText(order.CashOnDelivery ? "Sí" : "No");
        var banco = order.BillingAccount.BankName;
        var cuenta = order.BillingAccount.AccountNumber;
        var bancoYCuenta = BankAndAccount(banco, cuenta);
        var proofCells = Enumerable.Range(0, PaymentDateColumns)
            .SelectMany(index => ProofCells(proofs, index, publisher))
            .ToArray();
        var totalConsignado = ProofTotalCell(proofs);
        var formasDePago = Enumerable.Range(0, PaymentDateColumns)
            .Select(index => PaymentMethodCell(proofs, index, bancoYCuenta))
            .ToArray();

        foreach (var item in items)
        {
            context.Products.TryGetValue(item.VariantId, out var product);
            // Spec 2026-10-20 variantes §7: ambas salen de la foto de la línea (la variante que se vendió, con
            // su `Code` y su nombre visible); el catálogo de hoy sólo responde por una línea sin foto. La
            // línea no distingue la variante por defecto sin consultar Catalog —que Quotations no referencia—,
            // así que ahí «Variante» repite el nombre del producto, que es su nombre visible.
            var variante = ExportCell.OfText(QuotationItemProductLabel.NameOf(item.ProductName, product));
            yield return
            [
                ExportCell.OfText(empresa),
                ExportCell.OfText(QuotationItemProductLabel.CodeOf(item.ProductCode, product)),
                ExportCell.OfNumber(item.Quantity),
                ExportCell.OfNumber(item.DiscountedUnitPriceWithoutTax),
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
                ExportCell.OfText(fechaPedido),
                ExportCell.OfText(cliente),
                ExportCell.OfText(documentoIdentidad),
                ExportCell.OfText(bancoYCuenta),
                totalConsignado,
                ExportCell.OfNumber(item.TaxPercentage / 100m),
                nitEmpresa,
                ExportCell.OfText(ciudadCoordinadora),
                .. formasDePago,
                ExportCell.OfText(transportadora),
                ExportCell.OfNumber(order.Total),
                ExportCell.OfNumber(order.RetentionAmount),
                fleteValor,
                fleteSinIva,
                fleteIva,
                fleteModo,
                contraentrega,
                variante,
            ];
        }
    }

    private static ExportCell PaymentMethodCell(
        IReadOnlyList<OrderExportPaymentProof> proofs, int index, string bankAndAccount) =>
        ExportCell.OfText(index < proofs.Count ? bankAndAccount : string.Empty);

    private static string BankAndAccount(string bank, string account) =>
        string.Join(' ', new[] { bank, account }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static ExportCell ProofTotalCell(IReadOnlyList<OrderExportPaymentProof> proofs) =>
        proofs.Count == 0
            ? ExportCell.OfText(string.Empty)
            : ExportCell.OfNumber(proofs.Sum(proof => proof.Amount));

    // A nombre de quién sale la factura, con la precedencia de siempre: consumidor final, la parte
    // de facturación propia, la razón social si el pedido factura a ella, o el nombre de la ficha.
    private static string BilledNameFor(
        Order order, OrderParty? billing, QuotationCustomerRef? customer)
    {
        if (order.BillsToFinalConsumer)
        {
            return FinalConsumer.Name;
        }

        if (billing?.Name is { Length: > 0 } billingName)
        {
            return billingName;
        }

        if (order.BillingUsesBusinessName && !string.IsNullOrWhiteSpace(customer?.BusinessName))
        {
            return customer.BusinessName;
        }

        return customer?.Name ?? string.Empty;
    }

    private static string BilledIdentificationFor(
        Order order, OrderParty? billing, QuotationCustomerRef? customer) =>
        OrderBilledParty.SourceFor(order.BillsToFinalConsumer, billing) switch
        {
            BilledPartySource.FinalConsumer => FinalConsumer.IdentificationNumber,
            // Una parte guardada antes del número lo deja vacío, nunca con el del cliente, que es
            // otra persona.
            BilledPartySource.BillingParty => billing!.IdentificationNumber ?? string.Empty,
            _ => customer?.IdentificationNumber ?? string.Empty
        };

    private static (string Ciudad, string Direccion, string Telefono, string Email) ContactFor(
        OrderParty? shipping,
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

    private static string CoordinadoraCityFor(
        OrderParty? shipping,
        QuotationCustomerRef? customer,
        IReadOnlyDictionary<Guid, string> coordinadoraCityNames)
    {
        var cityId = shipping is not null ? shipping.CityId : customer?.CityId;
        return cityId is { } id && coordinadoraCityNames.TryGetValue(id, out var name)
            ? name
            : string.Empty;
    }

    // El NIT como lo importa el ERP: sin dígito de verificación, puntos ni espacios.
    private static string ErpTaxId(string? taxId)
    {
        if (string.IsNullOrEmpty(taxId))
        {
            return string.Empty;
        }

        var hyphen = taxId.IndexOf('-', StringComparison.Ordinal);
        var withoutCheckDigit = hyphen < 0 ? taxId : taxId[..hyphen];
        return string.Concat(withoutCheckDigit.Where(character => character != '.' && !char.IsWhiteSpace(character)));
    }

    private static ExportCell AdvisorCodeCell(
        Order order, IReadOnlyDictionary<Guid, QuotationAdvisor> advisors) =>
        advisors.TryGetValue(order.AdvisorId.Value, out var advisor)
            && advisor.AdvisorCode is { } code
                ? ExportCell.OfNumber(code)
                : ExportCell.OfText(string.Empty);

    private static ExportCell PaymentDateCell(
        IReadOnlyList<OrderExportPaymentProof> proofs, int index, TenantCalendar calendar)
    {
        if (index >= proofs.Count)
        {
            return ExportCell.OfText(string.Empty);
        }

        var proof = proofs[index];
        return ExportCell.OfText(proof.PaidOn is { } paidOn
            ? paidOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : calendar.ToLocal(proof.UploadedAt).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

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
