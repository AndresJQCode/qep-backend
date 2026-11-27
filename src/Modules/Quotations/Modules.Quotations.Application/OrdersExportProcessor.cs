using System.Globalization;
using BuildingBlocks.Domain.Taxes;
using FluentValidation;
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
    ITenantClock tenantClock,
    IQuotationPhoneNumbers phoneNumbers)
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
        var (status, paymentStatus, fulfillmentStatus, invoiced, source, kind) = ParseFilters(filters);
        var advisorId = filters.AdvisorId is { } advisor ? new MemberId(advisor) : (MemberId?)null;
        var clientIds = await OrderListing.ResolveClientIdsByCucAsync(
            customerLookup, job.TenantId, filters.ClientCuc, cancellationToken);
        // Un calendario por job (spec 2026-09-17): las fechas del archivo van en el día del tenant, porque las importa su
        // ERP. El rango corta por la fecha del pedido, que ya es un día del tenant (spec 2026-11-16 §4): la zona guardada
        // en el trabajo no lo mueve.
        var calendar = await tenantClock.GetAsync(job.TenantId, cancellationToken);
        var generatedAt = calendar.UtcNow;

        var storedLayout = await layoutRepository.FindAsync(job.TenantId, cancellationToken);
        var layout = OrdersExportLayoutProjection.For(OrdersExportLayout.Effective(storedLayout));
        var sheetName = OrdersExportLayout.EffectiveSheetName(storedLayout);
        Func<string, string> phoneText = OrdersExportLayout.EffectiveIncludePhoneDialCode(storedLayout)
            ? stored => stored
            : WithoutDialCode;

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
                fulfillmentStatus,
                invoiced,
                filters.ConvertedFrom,
                filters.ConvertedTo,
                filters.OrderNumber,
                source,
                kind,
                after,
                limit,
                ct),
            async (batch, ct) =>
            {
                var context = await LoadBatchContextAsync(job.TenantId, batch, ct);
                var rows = batch
                    .SelectMany(order => RowsFor(order, context, calendar, paymentProofPublisher, phoneText))
                    .Select(cells => layout.Project(cells))
                    .ToArray();
                exportedRows += rows.Length;
                return rows;
            },
            order => new OrderExportCursor(order.OrderDate, order.ConvertedAt, order.OrderNumber),
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

    // Spec 2026-11-25 indicativo: sólo se parte lo que empieza por «+»; sin él, o si no se puede partir,
    // sale como está guardado. El teléfono no va a ningún log.
    private string WithoutDialCode(string stored) =>
        stored.StartsWith('+') ? phoneNumbers.NationalNumberOf(stored) ?? stored : stored;

    // Un filtro ilegible es definitivo: el trabajo nació con él y reintentar no lo arregla.
    private static (
        OrderStatus? Status,
        OrderPaymentStatus? PaymentStatus,
        OrderFulfillmentStatus? FulfillmentStatus,
        bool? Invoiced,
        OrderSource? Source,
        QuotationKind? Kind) ParseFilters(
        OrdersExportFilters filters)
    {
        try
        {
            return (
                OrderListing.ParseStatus(filters.Status),
                OrderListing.ParsePaymentStatus(filters.PaymentStatus),
                OrderListing.ParseFulfillmentStatus(filters.FulfillmentStatus),
                OrderListing.ParseInvoiced(filters.Invoiced),
                OrderListing.ParseSource(filters.Source),
                OrderListing.ParseKind(filters.Kind));
        }
        catch (Exception exception) when (exception is QuotationsDomainException or ValidationException)
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
        IReadOnlyDictionary<Guid, string> CoordinadoraCityNames,
        IReadOnlyDictionary<Guid, string> DepartmentNames);

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

        // Un pedido guardado siempre tiene empresa emisora: sólo el cálculo previo nace sin ella, y no se
        // guarda (Order.Preview). De ahí el `!` acá y en RowsFor. La cuenta bancaria sí puede faltar (spec
        // 2026-11-17, R3): ver RowsFor.
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
        // Las ciudades de facturación entran en la misma consulta que las de envío (ajuste 2026-10-28).
        var billingParties = partiesByOrder.Values
            .SelectMany(parties => parties)
            .Where(party => party.Role == QuotationPartyRole.Billing)
            .ToArray();
        var billingCityIds = billingParties
            .Where(party => party.CityId is not null)
            .Select(party => party.CityId!.Value);
        var cityNames = await geographyLookup.FindCityNamesAsync(
            shippingCityIds.Concat(billingCityIds).Distinct().ToArray(), cancellationToken);
        // Los departamentos de envío entran en la misma consulta que los de facturación (ajuste 2026-10-28).
        var billingDepartmentIds = billingParties
            .Concat(partiesByOrder.Values
                .SelectMany(parties => parties)
                .Where(party => party.Role == QuotationPartyRole.Shipping))
            .Where(party => party.DepartmentId is not null)
            .Select(party => party.DepartmentId!.Value)
            .Distinct()
            .ToArray();
        var departmentNames = await geographyLookup.FindDepartmentNamesAsync(billingDepartmentIds, cancellationToken);

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
            advisors, coordinadoraCityNames, departmentNames);
    }

    // Una fila por línea del pedido. Un pedido sin líneas no puede existir (el dominio lo exige),
    // pero el procesador se defiende igual.
    // `phoneText` escribe las cuatro columnas de teléfono del catálogo (`phone`, `customer_phone`,
    // `billing_phone`, `shipping_phone`) según el interruptor del indicativo; una columna de teléfono nueva
    // pasa por él.
    private static IEnumerable<ExportCell[]> RowsFor(
        Order order,
        BatchContext context,
        TenantCalendar calendar,
        IPaymentProofPublisher publisher,
        Func<string, string> phoneText)
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
        var companyName = billingCompany?.Name ?? string.Empty;
        var companyTaxId = OrdersExportLayoutProjection.FixedCellFor(ErpTaxId(billingCompany?.TaxId));
        context.Customers.TryGetValue(order.ClientId, out var customer);
        var cuc = customer?.Cuc ?? string.Empty;
        var parties = context.PartiesByOrder.TryGetValue(order.Id, out var foundParties)
            ? foundParties
            : [];
        var shipping = parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Shipping);
        var billingParty = parties.FirstOrDefault(party => party.Role == QuotationPartyRole.Billing);
        var (city, address, phone, email) = ContactFor(shipping, customer, context.CityNames);
        var coordinadoraCity = CoordinadoraCityFor(shipping, customer, context.CoordinadoraCityNames);
        // «Cliente» es el nombre de la ficha; a nombre de quién sale la factura lo dice «Nombre facturación».
        var customerName = customer?.Name ?? string.Empty;
        var billingName = BilledNameFor(order, billingParty, customer);
        var identification = BilledIdentificationFor(order, billingParty, customer);
        var billingPersonType = BilledPersonTypeFor(order, billingParty, customer);
        var billingContact = BilledContactFor(order, billingParty, customer, context.CityNames, context.DepartmentNames);
        var shippingContact = ShippingContactFor(shipping, customer, context.CityNames, context.DepartmentNames);
        // D-R8 (spec 2026-10-23): ISO fijo a propósito. Esta hoja es el contrato con el importador del
        // ERP, no un documento para una persona: no sigue el formato de fecha del tenant. No lo «arregles».
        // Spec 2026-11-16 §5: la fecha del pedido, que el asesor puede corregir; ya es un día del tenant.
        var orderDate = order.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Spec 2026-10-15 §6.4 (y el hotfix del mismo día): las observaciones son las del pedido, que
        // al convertir heredó las de la cotización.
        var notes = order.Notes ?? string.Empty;
        var advisorCode = AdvisorCodeCell(order, context.Advisors);
        // "Transportadora" (spec 2026-10-05): sólo depende de la recogida. Las demás columnas de
        // entrega no cambian con ella (decisión del owner): sin parte de envío caen al cliente.
        // Spec 2026-10-17 flete (D-F8): la transportadora real del pedido; sin ella, "Coordinadora".
        var carrier = order.IsStorePickup
            ? StorePickupCarrier
            : string.IsNullOrWhiteSpace(order.CarrierName) ? DefaultCarrier : order.CarrierName;
        // Flete: con recogida o sin modo no hay valor ni modo. Al cobro deja vacío "Valor Flete" porque
        // el monto se paga a la transportadora y no es del documento; los desgloses salen en 0.
        var freightMode = order.IsStorePickup ? null : order.FreightChargeMode;
        var freightAmount = freightMode switch
        {
            FreightChargeMode.Charged => ExportCell.OfNumber(order.FreightAmount),
            FreightChargeMode.Free => ExportCell.OfNumber(0m),
            _ => ExportCell.OfText(string.Empty),
        };
        var freightWithoutTax = ExportCell.OfNumber(order.FreightSubtotal);
        var freightTax = ExportCell.OfNumber(order.FreightTaxAmount);
        var freightChargeMode = ExportCell.OfText(freightMode switch
        {
            FreightChargeMode.Charged => "Cobrado",
            FreightChargeMode.Free => "Gratis",
            FreightChargeMode.OnDelivery => "Al cobro",
            _ => string.Empty,
        });
        var cashOnDelivery = ExportCell.OfText(order.CashOnDelivery ? "Sí" : "No");
        // Una empresa sin cuenta (spec 2026-11-17 §4.2) deja vacías las celdas del banco, del número y de
        // payment_method_N: el ERP no recibe una cuenta que no existe.
        var bank = order.BillingAccount.BankName ?? string.Empty;
        var account = order.BillingAccount.AccountNumber ?? string.Empty;
        var bankAndAccount = BankAndAccount(bank, account);
        var proofCells = Enumerable.Range(0, PaymentDateColumns)
            .SelectMany(index => ProofCells(proofs, index, publisher))
            .ToArray();
        var proofAmountTotal = ProofTotalCell(proofs);
        var paymentMethods = Enumerable.Range(0, PaymentDateColumns)
            .Select(index => PaymentMethodCell(proofs, index, bankAndAccount))
            .ToArray();

        foreach (var item in items)
        {
            context.Products.TryGetValue(item.VariantId, out var product);
            // Spec 2026-10-20 variantes §7: ambas salen de la foto de la línea (la variante que se vendió, con
            // su `Code` y su nombre visible); el catálogo de hoy sólo responde por una línea sin foto. La
            // línea no distingue la variante por defecto sin consultar Catalog —que Quotations no referencia—,
            // así que ahí «Variante» repite el nombre del producto, que es su nombre visible.
            var variant = ExportCell.OfText(QuotationItemProductLabel.NameOf(item.ProductName, product));
            yield return
            [
                ExportCell.OfText(companyName),
                ExportCell.OfText(QuotationItemProductLabel.CodeOf(item.ProductCode, product)),
                ExportCell.OfNumber(item.Quantity),
                ExportCell.OfNumber(item.DiscountedUnitPriceWithoutTax),
                ExportCell.OfNumber(item.TaxAmount),
                // Desde 2026-11-23 el descuento de la línea es sin IVA, igual que el unitario y el IVA de la
                // fila, y la hoja del ERP lo recibe así. Decisión pendiente del owner: confirmar si el ERP
                // esperaba el descuento con IVA de antes.
                ExportCell.OfNumber(item.DiscountAmount),
                ExportCell.OfText(string.Empty), // Nota Detalle: no existe nota por línea.
                .. Enumerable.Range(0, PaymentDateColumns).Select(index => PaymentDateCell(proofs, index, calendar)),
                ExportCell.OfText(city),
                ExportCell.OfText(cuc),
                ExportCell.OfText(order.OrderNumber),
                ExportCell.OfText(address),
                ExportCell.OfText(notes),
                ExportCell.OfText(phoneText(phone)),
                ExportCell.OfText(email),
                advisorCode,
                ExportCell.OfText(bank),
                ExportCell.OfText(account),
                .. proofCells,
                ExportCell.OfNumber(item.UnitPriceWithoutTax),
                ExportCell.OfText(orderDate),
                ExportCell.OfText(bankAndAccount),
                proofAmountTotal,
                ExportCell.OfNumber(item.TaxPercentage / 100m),
                companyTaxId,
                ExportCell.OfText(coordinadoraCity),
                .. paymentMethods,
                ExportCell.OfText(carrier),
                ExportCell.OfNumber(order.Total),
                ExportCell.OfNumber(order.RetentionAmount),
                freightAmount,
                freightWithoutTax,
                freightTax,
                freightChargeMode,
                cashOnDelivery,
                variant,
                // Grupo customer_*: la ficha del cliente, nunca la parte de facturación ni el consumidor final.
                ExportCell.OfText(customerName),
                ExportCell.OfText(customer?.IdentificationType ?? string.Empty),
                ExportCell.OfText(customer?.IdentificationNumber ?? string.Empty),
                ExportCell.OfText(customer?.PersonType ?? string.Empty),
                ExportCell.OfText(customer?.Address ?? string.Empty),
                ExportCell.OfText(customer?.CityName ?? string.Empty),
                ExportCell.OfText(customer?.DepartmentName ?? string.Empty),
                ExportCell.OfText(phoneText(customer?.Phone ?? string.Empty)),
                ExportCell.OfText(customer?.Email ?? string.Empty),
                // Grupo billing_*: a quién se factura (OrderBilledParty.SourceFor).
                ExportCell.OfText(billingName),
                ExportCell.OfText(billingContact.IdentificationType),
                ExportCell.OfText(identification),
                ExportCell.OfText(billingPersonType),
                ExportCell.OfText(billingContact.Address),
                ExportCell.OfText(billingContact.City),
                ExportCell.OfText(billingContact.Department),
                ExportCell.OfText(phoneText(billingContact.Phone)),
                ExportCell.OfText(billingContact.Email),
                // Grupo shipping_*: a quién se despacha; sin parte de envío, la ficha del cliente (ShippingContactFor).
                ExportCell.OfText(shippingContact.Name),
                ExportCell.OfText(shippingContact.IdentificationType),
                ExportCell.OfText(shippingContact.IdentificationNumber),
                ExportCell.OfText(shippingContact.PersonType),
                ExportCell.OfText(shippingContact.Address),
                ExportCell.OfText(shippingContact.City),
                ExportCell.OfText(coordinadoraCity),
                ExportCell.OfText(shippingContact.Department),
                ExportCell.OfText(phoneText(shippingContact.Phone)),
                ExportCell.OfText(shippingContact.Email),
                WithholdingCell(order, TaxCountries.RetefuenteType),
                WithholdingCell(order, TaxCountries.ReteIcaType),
                ExportCell.OfText(order.BuyerNotes ?? string.Empty),
                WithholdingRateCell(order, TaxCountries.RetefuenteType),
                WithholdingRateCell(order, TaxCountries.ReteIcaType),
            ];
        }
    }

    /// <summary>La suma de lo retenido de ese tipo en el pedido; 0 si ninguna aplicó, como «Retencion».</summary>
    private static ExportCell WithholdingCell(Order order, string type) =>
        ExportCell.OfNumber(order.AppliedWithholdings
            .Where(withholding => string.Equals(withholding.Type, type, StringComparison.Ordinal))
            .Sum(withholding => withholding.Amount));

    /// <summary>
    /// La tarifa efectiva de ese tipo en porcentaje, tal como está guardada (spec 2026-11-16); 0 si ninguna aplicó. La
    /// ReteICA tampoco se escala a por mil (owner, 2026-11-16): 2,8 % sale 2.8, no 28. Con varias aplicadas del mismo
    /// tipo sin alcance sale la primera por <c>Position</c>: límite conocido, el owner confirmó que no se da hoy.
    /// Con alguna de alcance <c>Goods</c> o <c>Services</c> (spec 2026-11-18 §4) su base no es el subtotal y puede haber
    /// dos del tipo: sale la tarifa que da el monto de la columna sobre el subtotal del pedido, a tres decimales, para que
    /// el ERP que la multiplica por el subtotal llegue al mismo monto.
    /// </summary>
    private static ExportCell WithholdingRateCell(Order order, string type)
    {
        var applied = order.AppliedWithholdings
            .Where(withholding => string.Equals(withholding.Type, type, StringComparison.Ordinal))
            .ToList();
        if (applied.Count == 0)
        {
            return ExportCell.OfNumber(0m);
        }

        if (applied.All(withholding => withholding.AppliesTo == WithholdingScope.All))
        {
            return ExportCell.OfNumber(applied[0].Percentage);
        }

        return ExportCell.OfNumber(order.Subtotal == 0m
            ? 0m
            : Math.Round(applied.Sum(withholding => withholding.Amount) / order.Subtotal * 100m, 3, MidpointRounding.AwayFromZero));
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

    // Spec 2026-10-26 tipo de persona (reubicada el 2026-10-28 en «billing_person_type»): la misma fuente que
    // «Identificación facturación» (SourceFor), para que las dos celdas describan siempre a la misma persona. Sin filtro
    // de país: el Excel muestra lo guardado.
    private static string BilledPersonTypeFor(
        Order order, OrderParty? billing, QuotationCustomerRef? customer) =>
        OrderBilledParty.SourceFor(order.BillsToFinalConsumer, billing) switch
        {
            // El consumidor final sólo lleva su número genérico: el tipo de persona queda vacío (2026-10-28).
            BilledPartySource.FinalConsumer => string.Empty,
            // Una parte sin tipo lo deja vacío, nunca con el del cliente, que es otra persona.
            BilledPartySource.BillingParty => billing!.PersonType?.ToWireValue() ?? string.Empty,
            _ => customer?.PersonType ?? string.Empty
        };

    private readonly record struct BilledContact(
        string IdentificationType, string Address, string City, string Department, string Phone, string Email);

    // Ajuste 2026-10-28: los datos de contacto de quien se factura, con la misma fuente que «Documento de
    // identidad» (SourceFor). El consumidor final no tiene más que nombre y número genérico, así que acá
    // queda vacío. La parte de facturación es otra persona: lo que no trae queda vacío, nunca con lo del
    // cliente.
    private static BilledContact BilledContactFor(
        Order order,
        OrderParty? billing,
        QuotationCustomerRef? customer,
        IReadOnlyDictionary<Guid, string> cityNames,
        IReadOnlyDictionary<Guid, string> departmentNames) =>
        OrderBilledParty.SourceFor(order.BillsToFinalConsumer, billing) switch
        {
            BilledPartySource.FinalConsumer => new BilledContact(
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
            BilledPartySource.BillingParty => new BilledContact(
                billing!.IdentificationType?.ToWireValue() ?? string.Empty,
                billing.Address ?? string.Empty,
                CityOf(billing, cityNames),
                NameOf(billing.DepartmentId, departmentNames),
                billing.Phone ?? string.Empty,
                billing.Email ?? string.Empty),
            _ => new BilledContact(
                customer?.IdentificationType ?? string.Empty,
                customer?.Address ?? string.Empty,
                customer?.CityName ?? string.Empty,
                customer?.DepartmentName ?? string.Empty,
                customer?.Phone ?? string.Empty,
                customer?.Email ?? string.Empty)
        };

    private readonly record struct ShippingContact(
        string Name, string IdentificationType, string IdentificationNumber, string PersonType,
        string Address, string City, string Department, string Phone, string Email);

    // Ajuste 2026-10-28: con parte de envío, sus datos (lo que no trae queda vacío, nunca con lo del cliente);
    // sin ella, la ficha del cliente, la misma regla que ContactFor y CoordinadoraCityFor.
    private static ShippingContact ShippingContactFor(
        OrderParty? shipping,
        QuotationCustomerRef? customer,
        IReadOnlyDictionary<Guid, string> cityNames,
        IReadOnlyDictionary<Guid, string> departmentNames) =>
        shipping is not null
            ? new ShippingContact(
                shipping.Name ?? string.Empty,
                shipping.IdentificationType?.ToWireValue() ?? string.Empty,
                shipping.IdentificationNumber ?? string.Empty,
                shipping.PersonType?.ToWireValue() ?? string.Empty,
                shipping.Address ?? string.Empty,
                CityOf(shipping, cityNames),
                NameOf(shipping.DepartmentId, departmentNames),
                shipping.Phone ?? string.Empty,
                shipping.Email ?? string.Empty)
            : new ShippingContact(
                customer?.Name ?? string.Empty,
                customer?.IdentificationType ?? string.Empty,
                customer?.IdentificationNumber ?? string.Empty,
                customer?.PersonType ?? string.Empty,
                customer?.Address ?? string.Empty,
                customer?.CityName ?? string.Empty,
                customer?.DepartmentName ?? string.Empty,
                customer?.Phone ?? string.Empty,
                customer?.Email ?? string.Empty);

    // Una parte de afuera no tiene ciudad de DIVIPOLA: la columna lleva el texto que se escribió.
    private static string CityOf(OrderParty party, IReadOnlyDictionary<Guid, string> cityNames) =>
        party.Country is null ? NameOf(party.CityId, cityNames) : party.CityName ?? string.Empty;

    private static string NameOf(Guid? id, IReadOnlyDictionary<Guid, string> names) =>
        id is { } value && names.TryGetValue(value, out var name) ? name : string.Empty;

    private static (string Ciudad, string Direccion, string Telefono, string Email) ContactFor(
        OrderParty? shipping,
        QuotationCustomerRef? customer,
        IReadOnlyDictionary<Guid, string> cityNames)
    {
        if (shipping is not null)
        {
            return (CityOf(shipping, cityNames), shipping.Address ?? string.Empty, shipping.Phone ?? string.Empty, shipping.Email ?? string.Empty);
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

    // El documento como lo importa el ERP: sin puntos ni espacios; el guion y el dígito de
    // verificación se conservan (decisión del dueño, D11). No depende del tipo de documento.
    private static string ErpTaxId(string? taxId)
    {
        if (string.IsNullOrEmpty(taxId))
        {
            return string.Empty;
        }

        return string.Concat(taxId.Where(character => character != '.' && !char.IsWhiteSpace(character)));
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
