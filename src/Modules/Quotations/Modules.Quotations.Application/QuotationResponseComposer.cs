using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Arma la respuesta HTTP de una cotización con **todo lo que su pantalla muestra**: el cliente
/// con su libreta de direcciones, el correo de la asesora y, por cada línea, el nombre, la
/// portada y las escalas de su producto.
///
/// Existe porque el detalle costaba cuatro consultas del navegador para dibujar una pantalla:
/// la cotización, la ficha del cliente, el padrón de miembros entero para un correo y hasta
/// doscientos productos para poner nombre a sus líneas. Nada de eso es un recurso que la
/// persona esté mirando por separado — es esta cotización, contada completa.
///
/// Vive en el borde HTTP y no en el <c>QuotationDto</c> porque es una decisión de presentación:
/// los handlers siguen devolviendo el agregado y sus totales, sin cargar con lo que una pantalla
/// necesita para pintarse. Se aplica a **todas** las respuestas de cotización, no sólo al GET:
/// el frontend guarda en su caché lo que devuelve cada mutación, así que una respuesta sin estos
/// datos dejaría la pantalla a medias después de agregar una línea.
/// </summary>
public interface IQuotationResponseComposer
{
    Task<QuotationResponse> ComposeAsync(
        Guid tenantId, QuotationDto quotation, CancellationToken cancellationToken);
}

public sealed class QuotationResponseComposer(
    IQuotationCustomerLookup customerLookup,
    IQuotationAdvisorLookup advisorLookup,
    IQuotationProductLookup productLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationSettingsStore settingsStore,
    IQuotationWithholdingLookup withholdingLookup,
    IQuotationGeographyLookup geographyLookup,
    ITenantClock tenantClock)
    : IQuotationResponseComposer
{
    public async Task<QuotationResponse> ComposeAsync(
        Guid tenantId, QuotationDto quotation, CancellationToken cancellationToken)
    {
        var customer = await customerLookup.FindAsync(
            tenantId, quotation.ClientId, cancellationToken);

        var advisors = await advisorLookup.FindAsync(
            tenantId, [quotation.AdvisorId], cancellationToken);
        var advisor = advisors.GetValueOrDefault(quotation.AdvisorId);

        // Sólo cuando la cotización eligió empresa (con o sin cuenta): una consulta más por pantalla, y ninguna en el
        // caso normal de un borrador recién creado.
        var company = quotation.BillingAccount is { } billing
            ? await companyLookup.FindAsync(tenantId, billing.CompanyId, cancellationToken)
            : null;

        var products = await productLookup.FindManyAsync(
            tenantId,
            quotation.Items.Select(item => item.VariantId).Distinct().ToArray(),
            cancellationToken);

        // Here and not in QuotationDto: every quotation response passes through this composer, and it
        // is async with DI — the static mapper called by ~25 handlers is neither (plan decision A13).
        var settings = await settingsStore.GetAsync(tenantId, cancellationToken);

        var clientContacts = await ClientContactsAsync(customerLookup, tenantId, customer, cancellationToken);

        // Con parte de facturación propia la fuente vigente son sus selectores (Quotation.ActiveWithholdings).
        var ownBilling = quotation.Parties.Any(party => party.Role == nameof(QuotationPartyRole.Billing));
        var municipal = await MunicipalWithholdingExplanation.ExplainAsync(
            withholdingLookup,
            geographyLookup,
            tenantId,
            quotation.ClientId,
            quotation.BillingAccount,
            quotation.OperationCityId,
            ownBilling,
            ownBilling ? quotation.BillingWithholdingTaxRates : quotation.CustomerWithholdingTaxRates,
            MunicipalWithholdingExplanation.DateOf(quotation.CreatedAt),
            cancellationToken);
        var taxUnits = await TaxUnitContextAsync(
            tenantClock, withholdingLookup, tenantId, quotation.CreatedAt, quotation.WithholdingBreakdown, cancellationToken);
        var regionsById = await PartyRegionsAsync(geographyLookup, quotation.Parties, cancellationToken);

        return new QuotationResponse(
            quotation.Id,
            quotation.QuotationNumber,
            quotation.ClientId,
            ToClientResponse(customer),
            quotation.AdvisorId,
            advisor?.Email,
            advisor?.DisplayName,
            quotation.Status,
            quotation.CreatedAt,
            quotation.ValidUntil,
            quotation.PaymentMethod,
            quotation.Currency,
            quotation.Subtotal,
            quotation.TaxPercentage,
            quotation.TaxAmount,
            quotation.DiscountAmount,
            quotation.Total,
            quotation.CustomerVatSurplus,
            quotation.RetentionAmount,
            quotation.NetTotal,
            quotation.Notes,
            quotation.Parties.Select(party => ToPartyResponse(party, regionsById)).ToArray(),
            quotation.BillingUsesBusinessName,
            quotation.BillingWithRetention,
            quotation.BillingVatSurplus,
            quotation.IsStorePickup,
            quotation.BillsToFinalConsumer,
            ToBillingResponse(quotation.BillingAccount, company),
            quotation.CreatedBy,
            quotation.UpdatedBy,
            quotation.UpdatedAt,
            quotation.SentAt,
            quotation.PdfFileId,
            quotation.CanBeSent,
            quotation.HasChangesSinceSent,
            quotation.CanBeConvertedToOrder,
            ToMinimumPurchaseResponse(QuotationMinimumPurchase.DescribeFor(
                quotation.Items.Sum(item => item.Quantity), quotation.Subtotal + quotation.TaxAmount, quotation.Currency, settings)),
            quotation.Items
                .Select(item => ToItemResponse(item, products, quotation.GlobalScaleFloor))
                .ToArray(),
            quotation.Version,
            quotation.GlobalScaleFloor,
            quotation.IsRetail,
            AvailableFloors(quotation, products),
            ShippingResponse.From(quotation.Shipping),
            quotation.FreightSubtotal,
            quotation.FreightTaxAmount,
            [.. quotation.Withholdings.Select(ToWithholdingResponse)],
            [.. quotation.CustomerWithholdingTaxRates.Select(ToWithholdingTaxRateResponse)],
            [.. quotation.BillingWithholdingTaxRates.Select(ToWithholdingTaxRateResponse)],
            clientContacts,
            municipal.OperationCity,
            municipal.BillingCompanyCity,
            municipal.Entries,
            ToBreakdownResponse(quotation.WithholdingBreakdown, taxUnits),
            quotation.RequiresShipping,
            Kind: quotation.Kind,
            Project: quotation.Project);
    }

    /// <summary>Los contactos de la empresa del cliente (spec 2026-11-10 §4.6), en una consulta por documento. Un cliente que no
    /// resuelve no tiene contactos que mostrar. Lo usa también el compositor del pedido: se escribe una vez.</summary>
    internal static async Task<IReadOnlyList<ClientContactDto>> ClientContactsAsync(
        IQuotationCustomerLookup customerLookup, Guid tenantId, QuotationCustomerRef? customer, CancellationToken cancellationToken)
    {
        if (customer is null)
        {
            return [];
        }

        var contacts = await customerLookup.ListContactsAsync(tenantId, customer.Id, cancellationToken);
        return [.. contacts.Select(contact => new ClientContactDto(contact.Id, contact.Name, contact.Phone))];
    }

    /// <summary>
    /// Lo que el desglose necesita del calendario y de Catalog para decir si la UVT de cada fila está vieja y cuál tomaría
    /// hoy. El año del documento (spec retenciones por UVT §5.2) es el de su <c>CreatedAt</c> en la zona del tenant, la
    /// misma fecha con que se congeló la foto: el 31 de diciembre a las 20:00 en Bogotá ya es el año siguiente en UTC. El
    /// último año cargado por país (spec alerta UVT §5.4) sale de <see cref="IQuotationWithholdingLookup.TaxUnitsAsync"/>
    /// con esa misma fecha, la regla del refresco, para que lo que se anuncia sea lo que el guardado aplicaría.
    ///
    /// Perezoso: el calendario se lee sólo si alguna fila trae año o unidad, y Catalog sólo si alguna trae unidad, una vez
    /// por respuesta con los países distintos.
    /// </summary>
    internal static async Task<TaxUnitContext> TaxUnitContextAsync(
        ITenantClock tenantClock,
        IQuotationWithholdingLookup withholdingLookup,
        Guid tenantId,
        DateTimeOffset createdAt,
        IReadOnlyList<WithholdingVerdictDto>? breakdown,
        CancellationToken cancellationToken)
    {
        var rows = breakdown ?? [];
        if (!rows.Any(verdict => verdict.TaxUnitYear is not null || verdict.MinimumBaseUnit is not null))
        {
            return TaxUnitContext.None;
        }

        var documentDate = DocumentDates.LocalDate(await tenantClock.GetAsync(tenantId, cancellationToken), createdAt);
        string[] countries =
        [
            .. rows
                .Where(verdict => verdict.MinimumBaseUnit is not null)
                .Select(verdict => verdict.Country)
                .Distinct(StringComparer.Ordinal),
        ];
        if (countries.Length == 0)
        {
            return new TaxUnitContext(documentDate.Year, TaxUnitContext.None.LatestYearByCountry);
        }

        var units = await withholdingLookup.TaxUnitsAsync(countries, documentDate, cancellationToken);
        return new TaxUnitContext(
            documentDate.Year, units.ToDictionary(unit => unit.Key, unit => unit.Value.Year, StringComparer.Ordinal));
    }

    /// <summary>El desglose tal como viaja. Una fila sin año (sin unidad o legada sin casar) nunca está desactualizada, y
    /// una sin unidad no tiene último año que ofrecer.</summary>
    internal static IReadOnlyList<WithholdingBreakdownResponse> ToBreakdownResponse(
        IReadOnlyList<WithholdingVerdictDto>? breakdown, TaxUnitContext context) =>
        [
            .. (breakdown ?? []).Select(verdict => new WithholdingBreakdownResponse(
                verdict.TaxRateId,
                verdict.Name,
                verdict.Type,
                verdict.Country,
                verdict.Percentage,
                verdict.CatalogPercentage,
                verdict.PercentageOverridden,
                verdict.CityId,
                verdict.Applies,
                verdict.Reason,
                verdict.Base,
                verdict.MinimumBase,
                verdict.MinimumBaseUnit,
                verdict.MinimumBaseAmount,
                verdict.TaxUnitValue,
                verdict.TaxUnitYear,
                IsOutdated(verdict.TaxUnitYear, context.DocumentYear),
                verdict.MinimumBaseUnit is not null && context.LatestYearByCountry.TryGetValue(verdict.Country, out var latest)
                    ? latest
                    : null,
                verdict.Amount,
                verdict.AppliesTo)),
        ];

    internal static bool IsOutdated(int? taxUnitYear, int? documentYear) =>
        taxUnitYear is { } year && documentYear is { } document && year < document;

    // Internos por el mismo motivo que ToClientResponse: el detalle del pedido los pinta igual (OrderResponses).
    internal static WithholdingResponse ToWithholdingResponse(WithholdingDto withholding) => new(
        withholding.TaxRateId,
        withholding.Name,
        withholding.Type,
        withholding.Country,
        withholding.Percentage,
        withholding.MinimumBase,
        withholding.MinimumBaseUnit,
        withholding.TaxUnitValue,
        withholding.Base,
        withholding.Amount,
        withholding.CatalogPercentage,
        withholding.PercentageOverridden,
        withholding.CityId,
        withholding.AppliesTo);

    internal static WithholdingTaxRateResponse ToWithholdingTaxRateResponse(WithholdingTaxRateDto rate) =>
        new(rate.Id, rate.Name, rate.Type, rate.Country, rate.Percentage, rate.CatalogPercentage, rate.PercentageOverridden,
            rate.CityId, rate.AppliesTo);

    // Un cliente que no resuelve deja la cotización sin bloque de cliente en vez de tirar la
    // respuesta abajo: `ClientId` es una referencia blanda entre módulos, y una cotización
    // histórica tiene que poder leerse aunque su cliente se haya borrado.
    // Internos y no privados: el compositor del detalle del pedido (OrderResponseComposer) resuelve
    // cliente, líneas y cuenta de cobro con las mismas formas (spec 2026-10-15 §6.2).
    internal static QuotationClientResponse? ToClientResponse(QuotationCustomerRef? customer) =>
        customer is null
            ? null
            : new QuotationClientResponse(
                customer.Id,
                // Un incompleto no tiene CUC (spec 2026-10-10 §6.2), pero tampoco cotizaciones:
                // Quotations lo rechaza (T4). Esto sólo cubre el tipo y deja el contrato igual.
                customer.Cuc ?? string.Empty,
                customer.Name,
                customer.Phone,
                customer.Email,
                customer.Address,
                customer.CityId,
                customer.CityName,
                customer.DepartmentId,
                customer.DepartmentName,
                customer.BusinessName,
                customer.WithRetention,
                customer.VatSurplus,
                customer.IsActive,
                customer.UpdatedAt ?? default,
                (customer.Addresses ?? [])
                    .Select(address => new QuotationClientAddressResponse(
                        address.Id,
                        address.Name,
                        address.Address,
                        address.Phone,
                        address.CityId,
                        address.CityName,
                        address.DepartmentId,
                        address.DepartmentName,
                        address.IsPrincipal,
                        address.Country,
                        address.Region))
                    .ToArray(),
                customer.Country,
                customer.PersonType,
                customer.IdentificationType,
                customer.Region,
                (customer.BillingProfiles ?? []).Select(ToBillingProfileResponse).ToArray(),
                customer.BillingProfileLimit,
                customer.IdentificationNumber);

    private static QuotationClientBillingProfileResponse ToBillingProfileResponse(
        QuotationCustomerBillingProfileRef profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.PersonType,
            profile.IdentificationType,
            profile.IdentificationNumber,
            profile.Phone,
            profile.Email,
            profile.Address,
            profile.Country,
            profile.CityId,
            profile.CityName,
            profile.DepartmentId,
            profile.DepartmentName,
            profile.Region,
            profile.WithholdingTaxRates,
            profile.VatSurplus,
            profile.IsPrincipal);

    // La variante puede no existir más (se borró): la línea conserva lo que la cotización
    // congeló —cantidad, precio, descuento— y el nombre queda vacío, que es lo único que este lado
    // no puede reconstruir. El diccionario va por id de variante (spec 2026-10-20 §4).
    internal static QuotationItemResponse ToItemResponse(
        QuotationItemDto item,
        IReadOnlyDictionary<Guid, QuotationVariantRef> products,
        int? globalScaleFloor)
    {
        products.TryGetValue(item.VariantId, out var product);

        return new QuotationItemResponse(
            item.Id,
            item.VariantId,
            item.ProductId,
            // Congelados si la cotización ya salió del borrador; si no, los del catálogo de hoy.
            // Portada y escalas siguen siendo las de hoy: no se congelan.
            QuotationItemProductLabel.NameOf(item.ProductName, product),
            QuotationItemProductLabel.CodeOf(item.ProductCode, product),
            product?.ImageUrl,
            // Ordenadas acá y no en el adaptador: el orden es parte del contrato de esta
            // respuesta —la pantalla pinta la tabla de descuentos tal como llega— y Catalog no
            // promete ninguno, así que confiar en el que traiga la consulta deja el orden atado
            // a un detalle de almacenamiento.
            (product?.Scales ?? [])
                .OrderBy(scale => scale.FromUnit)
                .Select(scale => new QuotationItemPriceScaleResponse(
                    scale.FromUnit,
                    scale.ToUnit,
                    scale.Discount,
                    ToWireValue(scale.Restriction),
                    scale.Multiple,
                    scale.PackagingUnits))
                .ToArray(),
            item.Quantity,
            item.UnitPrice,
            item.DiscountPercentage,
            item.DiscountAmount,
            item.DiscountedUnitPrice,
            item.Subtotal,
            item.TaxPercentage,
            item.TaxAmount,
            item.Position,
            item.DiscountOrigin,
            // Con el producto borrado no hay escalas contra las que explicar nada: decir
            // "no tiene ese tramo" sería inventar.
            product is null
                ? null
                : QuotationGlobalScaleFloorMiss.Explain(
                    item.Quantity,
                    item.DiscountPercentage,
                    item.DiscountOrigin,
                    product.Scales,
                    globalScaleFloor),
            // Los de la línea, nunca los del catálogo de hoy (spec 2026-11-18 §3.4).
            item.Kind,
            item.SalesUnit);
    }

    /// <summary>
    /// Los pisos que el select del descuento global puede ofrecer. Sale de los productos que el
    /// composer ya cargó para poner nombre y escalas a cada línea, así que no cuesta una
    /// consulta más — y por eso no hay endpoint aparte: el dato ya viajaba, desparramado en las
    /// escalas de cada línea.
    ///
    /// No se ofrece un piso que ningún producto de esta cotización tiene: elegirlo no
    /// descontaría nada y no habría forma de explicar por qué.
    /// </summary>
    private static int[] AvailableFloors(
        QuotationDto quotation,
        IReadOnlyDictionary<Guid, QuotationVariantRef> products) =>
        AvailableFloors(quotation.Items, products);

    internal static int[] AvailableFloors(
        IReadOnlyCollection<QuotationItemDto> items,
        IReadOnlyDictionary<Guid, QuotationVariantRef> products) =>
        items
            .Select(item => item.VariantId)
            .Distinct()
            .SelectMany(variantId => products.TryGetValue(variantId, out var product)
                ? product.Scales.Select(scale => scale.FromUnit)
                : Enumerable.Empty<int>())
            .Distinct()
            .OrderBy(floor => floor)
            .ToArray();

    // La empresa que no resuelve deja el bloque con la cuenta guardada y sin razón social, por
    // el mismo motivo que el cliente: la cotización es histórica y tiene que poder leerse aunque
    // la empresa se haya borrado. Lo que se muestra es lo que se congeló, no lo que hay hoy.
    // Una empresa sin cuenta (spec 2026-11-17, R3) se responde con la empresa y banco y número nulos: la pantalla pinta
    // «Pago: por definir».
    internal static QuotationBillingResponse? ToBillingResponse(
        QuotationBillingAccountDto? account, QuotationCompanyRef? company) =>
        account is null
            ? null
            : new QuotationBillingResponse(
                account.CompanyId,
                company?.Name,
                company?.TaxId,
                company?.DocumentType,
                company?.Address,
                company?.Phone,
                account.BankName,
                account.AccountNumber,
                account.Currency,
                account.ChargesVat);

    // Field-by-field copy: the computation is QuotationMinimumPurchase.DescribeFor, the single
    // source of truth.
    private static QuotationMinimumPurchaseResponse ToMinimumPurchaseResponse(
        QuotationMinimumPurchaseDto minimum) => new(
            minimum.Met,
            minimum.Units,
            minimum.MinimumUnits,
            minimum.Currency,
            minimum.MinimumTotal,
            minimum.MissingUnits,
            minimum.MissingTotal);

    /// <summary>The names of the regions the parties carry, in one geography call (none when no party has a region).
    /// Internal because the order composer resolves its parties the same way.</summary>
    internal static async Task<IReadOnlyDictionary<Guid, QuotationRegionRef>> PartyRegionsAsync(
        IQuotationGeographyLookup geographyLookup,
        IEnumerable<QuotationPartyDto> parties,
        CancellationToken cancellationToken)
    {
        Guid[] regionIds = [.. parties.Where(party => party.RegionId is not null).Select(party => party.RegionId!.Value).Distinct()];
        return regionIds.Length == 0
            ? new Dictionary<Guid, QuotationRegionRef>()
            : await geographyLookup.FindRegionsAsync(regionIds, cancellationToken);
    }

    internal static QuotationRegionDto? ToRegionDto(
        Guid? regionId, IReadOnlyDictionary<Guid, QuotationRegionRef> regionsById) =>
        regionId is { } id && regionsById.TryGetValue(id, out var region)
            ? new QuotationRegionDto(region.Id, region.Name)
            : null;

    private static QuotationPartyResponse ToPartyResponse(
        QuotationPartyDto party, IReadOnlyDictionary<Guid, QuotationRegionRef> regionsById) => new(
        party.Id,
        party.Role,
        party.Name,
        party.IdentificationNumber,
        party.Phone,
        party.Email,
        party.Address,
        party.DepartmentId,
        party.CityId,
        party.PersonType,
        party.IdentificationType,
        party.Country,
        party.CityName,
        ToRegionDto(party.RegionId, regionsById),
        party.PostalCode);

    // Mismos literales que Catalog pone en PriceScaleResponse.Restriction: el diccionario lo
    // tiene el frontend y tiene que ser uno solo para los dos módulos. Null también igual que
    // allá: leer una cotización cuyo producto quedó con escalas incompletas no puede romper.
    private static string? ToWireValue(QuotationPriceScaleRestriction? restriction) => restriction switch
    {
        null => null,
        QuotationPriceScaleRestriction.Multiple => "multiple",
        QuotationPriceScaleRestriction.PackagingUnit => "packaging_unit",
        _ => throw new ArgumentOutOfRangeException(nameof(restriction))
    };
}

/// <summary>
/// El año del documento y el último año de UVT cargado por país, que <see cref="QuotationResponseComposer.ToBreakdownResponse"/>
/// necesita y un mapeo estático no puede leer (spec alerta UVT §5.4). <see cref="None"/> es «ninguna fila trae año ni
/// unidad»: no se leyó nada y no hay nada que decir.
/// </summary>
internal sealed record TaxUnitContext(int? DocumentYear, IReadOnlyDictionary<string, int> LatestYearByCountry)
{
    public static TaxUnitContext None { get; } = new(null, new Dictionary<string, int>());
}
