using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modules.Quotations.Application;

public sealed record QuotationItemDto(
    Guid Id,
    /// <summary>La variante que vende la línea (spec 2026-10-20 §4); <c>ProductId</c> es su producto.</summary>
    Guid VariantId,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    /// <summary>Lo que cuesta cada unidad ya con el descuento aplicado, IVA adentro — misma
    /// unidad que <c>UnitPrice</c>, no la de <c>Subtotal</c>. Lo calcula la línea
    /// (<c>QuotationItem.DiscountedUnitPriceUnder</c>, con la regla de redondeo del documento) y no quien lo muestra: la pantalla y el PDF lo
    /// derivaban cada uno por su cuenta.</summary>
    decimal DiscountedUnitPrice,
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position,
    /// <summary>El nombre de <c>QuotationDiscountOrigin</c>: <c>Own</c>, <c>Group</c> o
    /// <c>GlobalFloor</c>. Los enums viajan con su nombre porque el diccionario lo tiene el
    /// frontend. Sin esto la pantalla no puede explicar por que una linea de 3 unidades
    /// descuenta 12%.</summary>
    string DiscountOrigin,
    /// <summary>Código y nombre congelados al salir del borrador; null en un borrador y en las
    /// líneas enviadas antes de que existieran. No viajan así al cliente HTTP: el composer los
    /// resuelve contra el catálogo con <see cref="QuotationItemProductLabel"/>.</summary>
    string? ProductCode,
    string? ProductName,
    /// <summary>El tipo y la unidad que la línea fijó al nacer (spec 2026-11-18 §3.4), por nombre.</summary>
    string Kind,
    string SalesUnit,
    /// <summary>Mayorista fase 3: la línea de la orden de compra de la que salió, sólo en un pedido mayorista.</summary>
    Guid? SourcePurchaseOrderLineId = null,
    /// <summary>Regalo (spec 2026-11-21): la línea conserva su precio de lista pero se cobra a 0. Viaja porque la pantalla la
    /// dibuja distinta y no puede deducirlo de un descuento del 100 %, que también puede escribirse a mano.</summary>
    bool IsGift = false);

public sealed record QuotationDto(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    Guid AdvisorId,
    // Status es texto y no el enum del dominio: ningún DTO expone un enum de dominio
    // directamente, mismo criterio que PriceScaleResponse.Restriction en Catalog.
    string Status,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    /// <summary>La moneda de todos los importes de abajo: un código del catálogo de monedas. La fija la cuenta de
    /// cobro de la cotizacion.</summary>
    string Currency,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    // CustomerVatSurplus viaja para que el frontend pueda mostrar "exento por excedente de
    // IVA" en vez de adivinar por que TaxAmount dio cero. Es el valor **efectivo**
    // (Quotation.AppliesVatSurplus), no el snapshot del cliente: facturando a consumidor final
    // el IVA se cobra aunque el cliente tenga excedente, y la pantalla y el PDF imprimen "exento"
    // con solo ver esto en true. RetentionAmount/NetTotal son el snapshot de retencion en la
    // fuente (Quotation.RecalculateTotals): NetTotal = Total - RetentionAmount es lo que
    // efectivamente se cobra en efectivo.
    bool CustomerVatSurplus,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    /// <summary>Sólo las partes que difieren del cliente. Una cotización que factura y entrega
    /// a los datos del cliente llega con la lista vacía.</summary>
    IReadOnlyCollection<QuotationPartyDto> Parties,
    /// <summary>Si la facturación sigue al cliente, si va con su razón social.</summary>
    bool BillingUsesBusinessName,
    /// <summary>Derivado para el frontend viejo (spec impuestos por tipo §9.3): con fila de facturación
    /// propia en <c>Parties</c>, <c>BillingWithholdingTaxRates.Count > 0</c>; con los datos del cliente, null.
    /// <b>Nunca null con parte propia</b>: el frontend viejo bloquea «Convertir en pedido» mientras sea null
    /// (<c>qep-frontend/src/features/quotes/utils/quote-actions.ts:104-106</c>), y la conversión ya no exige
    /// contestarlo. En el cuerpo se ignora.</summary>
    bool? BillingWithRetention,
    /// <summary>Mismo criterio que <c>BillingWithRetention</c> pero para el excedente de IVA
    /// (<c>Quotation.PartyVatSurplus</c>).</summary>
    bool? BillingVatSurplus,
    /// <summary>Si el cliente recoge en la tienda. Cuando es true <c>Parties</c> nunca trae la
    /// parte de entrega.</summary>
    bool IsStorePickup,
    /// <summary>Si la factura sale a nombre de consumidor final. Cuando es true <c>Parties</c>
    /// nunca trae la parte de facturación y <c>BillingUsesBusinessName</c> es false.</summary>
    bool BillsToFinalConsumer,
    /// <summary>Con qué empresa y a qué cuenta se cobra. Null mientras nadie la eligió.</summary>
    QuotationBillingAccountDto? BillingAccount,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    /// <summary>Si tiene sentido ofrecer "enviar" ahora: un borrador y una ya enviada, haya
    /// cambiado o no — reenviar sin cambios es un caso legítimo.</summary>
    bool CanBeSent,
    /// <summary>Si se editó después del último envío. Viaja aunque
    /// <see cref="CanBeConvertedToOrder"/> ya lo incluya: es el único de los motivos de ese
    /// false que la pantalla no puede deducir de los otros campos, y sin él "Convertir en
    /// pedido" desaparece sin decir por qué (la pantalla enumera los otros tres).</summary>
    bool HasChangesSinceSent,
    /// <summary>Si convertir en pedido es posible: enviada, sin cambios desde ese envío, con
    /// productos, vigencia, forma de pago y cuenta de cobro.</summary>
    bool CanBeConvertedToOrder,
    IReadOnlyCollection<QuotationItemDto> Items,
    /// <summary>La versión del agregado con la que se leyó, para mandarla en <c>If-Match</c> al
    /// guardar de una vez (<c>PUT /quotations/{quotationId}</c>). Ya existía en el dominio
    /// (<c>Quotation.Version</c>) y no salía de la aplicación: sin ella la pantalla no tiene con
    /// qué probar que está guardando sobre lo que leyó. Va al final por ser aditiva.</summary>
    long Version,
    /// <summary>La entrega por envío, con forma fija también en recogida. Ver <see cref="ShippingDto"/>.</summary>
    ShippingDto Shipping,
    /// <summary>Envío sin IVA que suma a <c>Total</c>; 0 salvo <c>Charged</c>. Junto a <c>Subtotal</c> y
    /// <c>TaxAmount</c>, que siguen siendo sólo productos, explica el total (spec 2026-10-17 §5.3).</summary>
    decimal FreightSubtotal,
    /// <summary>IVA del envío que suma a <c>Total</c>; 0 con excedente de IVA o fuera de COP.</summary>
    decimal FreightTaxAmount,
    /// <summary>Sólo las retenciones que aplicaron, de la fuente vigente (spec impuestos por tipo §9.3); su suma es
    /// <c>RetentionAmount</c>.</summary>
    IReadOnlyList<WithholdingDto> Withholdings,
    /// <summary>Las tasas del cliente congeladas al crear o al cambiar de cliente, apliquen o no.</summary>
    IReadOnlyList<WithholdingTaxRateDto> CustomerWithholdingTaxRates,
    /// <summary>Los selectores de la facturación propia, apliquen o no. Se conservan aunque la cotización vuelva a los
    /// datos del cliente (spec §7.5).</summary>
    IReadOnlyList<WithholdingTaxRateDto> BillingWithholdingTaxRates,
    /// <summary>El piso de escala global elegido, o null. Ver
    /// <c>Quotation.GlobalScaleFloor</c>. Con default para no tocar las construcciones que ya
    /// existen: solo QuotationMapping.ToDto lo llena.</summary>
    int? GlobalScaleFloor = null,
    /// <summary>Cotización detal: ninguna línea recibe descuento, cualesquiera sean las escalas
    /// de su producto. Ver <c>Quotation.IsRetail</c>. Con default por el mismo motivo que
    /// <see cref="GlobalScaleFloor"/>.</summary>
    bool IsRetail = false,
    /// <summary>El municipio de la operación (spec ReteICA por empresa §7.5); null sin empresa, fuera de un país con tipos
    /// municipales o en un documento anterior. El compositor lo resuelve a nombre y departamento.</summary>
    Guid? OperationCityId = null,
    /// <summary>El veredicto de cada tasa de la fuente vigente, aplique o no, en su orden (spec retenciones por UVT §6).
    /// QuotationMapping.ToDto la llena siempre; null sólo en un DTO armado a mano, y el compositor lo lee como vacío.</summary>
    IReadOnlyList<WithholdingVerdictDto>? WithholdingBreakdown = null,
    /// <summary>Ver <c>Quotation.RequiresShipping</c> (spec 2026-11-18 §5). Con default por el mismo motivo que los de
    /// arriba: QuotationMapping.ToDto la llena siempre.</summary>
    bool RequiresShipping = true,
    /// <summary>El tipo de documento, por nombre: <c>Standard</c> o <c>Project</c> (spec 2026-11-26 §6.2).</summary>
    string Kind = "Standard",
    /// <summary>El alcance y el precio de un proyecto; null en una <c>Standard</c>.</summary>
    ProjectScopeDto? Project = null);

/// <summary>Una retención que aplicó (spec impuestos por tipo §9.3): la tasa congelada, la base sobre la que se
/// calculó y su monto. <c>Type</c> viaja como texto ("Retefuente", "ReteICA").</summary>
/// <param name="MinimumBase">En unidades tributarias si <c>MinimumBaseUnit</c> no es null, en dinero si no; null sin
/// base mínima.</param>
/// <param name="TaxUnitValue">El valor de la unidad congelado para la fecha del documento.</param>
public sealed record WithholdingDto(
    Guid TaxRateId,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? TaxUnitValue,
    decimal Base,
    decimal Amount,
    /// <summary>El porcentaje del catálogo al congelar; <c>Percentage</c> es el efectivo (spec ReteICA por empresa §9.4).
    /// Viajan los dos para que la pantalla muestre «editado» sin guardar el original por su cuenta.</summary>
    decimal CatalogPercentage,
    bool PercentageOverridden,
    /// <summary>El municipio de una municipal; null en las nacionales.</summary>
    Guid? CityId,
    /// <summary>El nombre de <c>WithholdingScope</c> de la tasa (spec 2026-11-18 §4.4): con dos tasas del mismo nombre, una
    /// por alcance, es lo que dice cuál retiene sobre bienes y cuál sobre servicios.</summary>
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <summary>Una tasa de retención congelada en el documento, aplique o no: lo que prellena un selector. Con el porcentaje
/// del catálogo y el municipio, porque el editor de porcentaje lee la fila aunque no haya aplicado (spec §9.4).</summary>
/// <param name="AppliesTo">El nombre de <c>WithholdingScope</c> con el que se congeló la tasa: <c>All</c>, <c>Goods</c> o
/// <c>Services</c> (spec 2026-11-18 servicios pendientes §2). Sale de la foto del documento y no del catálogo: el selector
/// dice «Retefuente (servicios)» aunque la tasa ya no esté en el catálogo activo. Va al final y con default para no tocar
/// las construcciones existentes.</param>
public sealed record WithholdingTaxRateDto(
    Guid Id,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal CatalogPercentage,
    bool PercentageOverridden,
    Guid? CityId,
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <summary>
/// Por qué una tasa de la fuente vigente aplica o no (spec retenciones por UVT §5.1, §6): la tasa congelada, el motivo, la
/// base que se comparó (el subtotal sin IVA, aunque no aplique), el umbral en dinero y la UVT con su año. Lo decide el
/// dominio (<c>WithholdingVerdict</c>); el frontend sólo pinta.
/// </summary>
/// <param name="Reason">El nombre de <c>WithholdingReason</c>: <c>Applied</c>, <c>BelowMinimumBase</c>,
/// <c>NoTaxUnitValue</c>, <c>FinalConsumer</c>, <c>NoBillingCompany</c>, <c>CurrencyWithoutTax</c>,
/// <c>OtherMunicipality</c> o <c>NoLinesOfScope</c> (spec 2026-11-18 §4.2: una tasa de bienes o de servicios sin líneas de
/// ese tipo).</param>
/// <param name="MinimumBaseAmount">El umbral en dinero; null sin base mínima o con base en UVT y sin valor cargado.</param>
/// <param name="TaxUnitYear">El año de la UVT congelada; null sin unidad o en una fila anterior que no casó (§8.2).</param>
/// <param name="Amount">Lo que guardó el último recálculo; 0 si no aplicó.</param>
/// <param name="AppliesTo">El nombre de <c>WithholdingScope</c> de la tasa congelada (spec 2026-11-18 §4.4): <c>All</c>,
/// <c>Goods</c> o <c>Services</c>. Viaja para que la pantalla diga sobre qué líneas se calculó <c>Base</c> sin deducirlo
/// del nombre de la tasa.</param>
public sealed record WithholdingVerdictDto(
    Guid TaxRateId,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal CatalogPercentage,
    bool PercentageOverridden,
    Guid? CityId,
    bool Applies,
    string Reason,
    decimal Base,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? MinimumBaseAmount,
    decimal? TaxUnitValue,
    int? TaxUnitYear,
    decimal Amount,
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <summary>
/// Gemelo HTTP de <see cref="WithholdingVerdictDto"/> más <see cref="TaxUnitOutdated"/>, que sale del calendario del
/// tenant y por eso lo calcula el compositor, no el mapeo estático.
/// </summary>
/// <param name="TaxUnitOutdated">Si la UVT congelada es de un año anterior al del documento (su <c>CreatedAt</c> en la
/// zona del tenant). False con <c>TaxUnitYear</c> null.</param>
/// <param name="TaxUnitLatestYear">El año de la UVT que hoy tomaría la fila si se refrescara: la que Catalog da para su país
/// con la fecha del documento (spec alerta UVT §5.4). Null sin base en unidades o sin ningún valor cargado para el país. Va
/// aparte de <c>TaxUnitOutdated</c> para que la pantalla distinga «falta cargar la UVT» de «ya está cargada y se aplicará
/// al guardar» sin calcular nada.</param>
public sealed record WithholdingBreakdownResponse(
    Guid TaxRateId,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal CatalogPercentage,
    bool PercentageOverridden,
    Guid? CityId,
    bool Applies,
    string Reason,
    decimal Base,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? MinimumBaseAmount,
    decimal? TaxUnitValue,
    int? TaxUnitYear,
    bool TaxUnitOutdated,
    int? TaxUnitLatestYear,
    decimal Amount,
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <summary>
/// La compra mínima que habilita cualquier descuento de escala, tal como la ve la pantalla.
///
/// Existe por el mismo motivo que <c>QuotationDto.CustomerVatSurplus</c>: sin esto el frontend ve
/// un descuento en cero y tiene que adivinar por qué. Y adivinar no le alcanza — el umbral depende
/// de la moneda, la regla es un OR de dos ramas, y ninguna de las dos cosas se deduce mirando los
/// importes.
///
/// <b>Es prospectivo, no retrospectivo.</b> Dice "te faltan 4 unidades o $480.000 para acceder a
/// los descuentos", no "perdiste un descuento". La diferencia importa: una cotización que se lee
/// de la base ya tiene sus descuentos en cero y **no guarda rastro** de cuál era el candidato que
/// la compuerta se llevó; reconstruirlo obligaría a consultar el catálogo en cada lectura. Lo que
/// sí se puede afirmar sin ir a ningún lado —y es lo accionable para quien cotiza— es cuánto falta
/// para el umbral.
///
/// <b>Los faltantes se calculan acá y no en el cliente</b> para que dos pantallas no redondeen
/// distinto, mismo criterio que <c>/reports/orders/summary</c>.
/// </summary>
/// <param name="Met">Si la cotización ya habilita descuentos. Con cualquiera de las dos ramas
/// cumplida es true, y los dos faltantes son 0.</param>
/// <param name="Units">Suma de las cantidades de todas las líneas.</param>
/// <param name="MinimumUnits">Las unidades que habilitan el descuento por sí solas.</param>
/// <param name="Currency">The quotation currency, so the screen formats both totals without asking.</param>
/// <param name="MinimumTotal">El total que habilita el descuento por sí solo, **en la moneda de
/// la cotización** (<paramref name="Currency"/>). No es una conversión: este módulo no tiene tabla
/// de cambio, así que el mínimo de cada moneda es un número propio del tenant. Null when the
/// tenant has no minimum total for this currency: the gate only opens by units (spec D7).</param>
/// <param name="MissingUnits">Cuántas unidades faltan para <paramref name="MinimumUnits"/>. 0
/// cuando <paramref name="Met"/>.</param>
/// <param name="MissingTotal">Cuánta plata falta para <paramref name="MinimumTotal"/>, contra el
/// total ya descontado y con IVA. 0 cuando <paramref name="Met"/>. Null when the tenant has no
/// minimum total for this currency: the gate only opens by units (spec D7).</param>
public sealed record QuotationMinimumPurchaseDto(
    bool Met,
    decimal Units,
    decimal MinimumUnits,
    string Currency,
    decimal? MinimumTotal,
    decimal MissingUnits,
    decimal? MissingTotal);

/// <summary>The tenant's minimum purchase (spec 2026-10-08, D7), totals in catalogue order.</summary>
public sealed record QuotationSettingsDto(int MinimumUnits, IReadOnlyDictionary<string, decimal> MinimumTotals);

/// <summary>Una parte (facturación o entrega) tal como sale hacia el cliente HTTP. Role es texto
/// y no el enum del dominio, mismo criterio que Status.</summary>
public sealed record QuotationPartyDto(
    Guid Id,
    string Role,
    string? Name,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId,
    /// <summary>"Natural" o "Juridica"; sólo en facturación (spec 2026-10-26), siempre null en la entrega.</summary>
    string? PersonType = null,
    /// <summary>Uno de los siete códigos de documento (NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID); sólo en facturación, siempre null en la entrega.</summary>
    string? IdentificationType = null,
    /// <summary>ISO alfa-2. En el envío siempre viaja: una parte sin país es <c>CO</c>. En la facturación <c>null</c> es
    /// <b>país pendiente</b> (spec 2026-11-27): una fila con nombre que nunca tuvo país ni ciudad; la pantalla muestra el aviso
    /// y no deja guardar sin elegirlo. Una fila vieja con ciudad viaja <c>CO</c>.</summary>
    string? Country = null,
    /// <summary>La ciudad escrita, sólo fuera de Colombia; null en Colombia.</summary>
    string? CityName = null,
    /// <summary>The region id of a party outside Colombia; the composers resolve it to a name.</summary>
    Guid? RegionId = null,
    /// <summary>Only on the shipping party; always null on billing.</summary>
    string? PostalCode = null);

/// <summary>La cuenta con la que se factura, tal como sale hacia el cliente HTTP. Es la copia
/// guardada, no lo que la empresa tenga hoy.</summary>
public sealed record QuotationBillingAccountDto(
    Guid CompanyId,
    /// <summary>Null, junto con <c>AccountNumber</c>, en una empresa sin cuenta (spec 2026-11-17 §4.2): la pantalla
    /// pinta «Pago: por definir».</summary>
    string? BankName,
    string? AccountNumber,
    string Currency,
    /// <summary>El país de la empresa congelado con la cuenta: decide qué tipos municipales se explican (spec ReteICA por
    /// empresa §9.4) sin volver a Companies.</summary>
    string? Country = null,
    /// <summary>El municipio de la empresa congelado con la cuenta (<c>billing_company_city_id</c>, A12); null en un
    /// documento anterior que todavía no reenvió la cuenta.</summary>
    Guid? CityId = null,
    /// <summary>Si la empresa emisora cobra IVA, congelado con la cuenta (spec 2026-11-17 §5.2).</summary>
    bool ChargesVat = true);

/// <summary>
/// La cuenta elegida, tal como viaja en el request. Llegan los cuatro campos y no sólo el id de
/// la empresa porque una <c>CompanyBankAccount</c> no tiene identidad propia: la terna
/// banco/número/moneda es lo único que la distingue dentro de su empresa. El handler la verifica
/// contra las cuentas de esa empresa antes de copiarla — el cuerpo lo escribe el cliente.
///
/// Banco y número nulos los dos es «empresa sin cuenta» (spec 2026-11-17, R3); ahí la moneda la decide el documento y
/// <c>Currency</c> se ignora.
/// </summary>
public sealed record QuotationBillingAccountRequest(
    Guid CompanyId,
    string? BankName,
    string? AccountNumber,
    string? Currency);

/// <summary>Los datos de una parte tal como viajan en el request (US-6). Cada campo null es
/// "para éste, el del cliente".
///
/// <c>IdentificationNumber</c> es la excepción: en facturación, un <c>Name</c> propio lo vuelve
/// obligatorio —422 <c>validation.failed</c> con <c>errors["Parties.Billing.IdentificationNumber"]</c>—
/// porque la factura sale a otra persona y sin su documento no se puede facturar. En la entrega
/// se ignora.</summary>
public sealed record QuotationPartyRequest(
    string? Name,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId,
    /// <summary>"Natural" o "Juridica" (spec 2026-10-26). Sólo en facturación; en la entrega se ignora.</summary>
    string? PersonType = null,
    /// <summary>Uno de los siete códigos (NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID; qué país acepta cuáles lo dice <c>DocumentTypesByCountry</c>): el tipo de <c>IdentificationNumber</c>. En facturación con
    /// nombre propio es obligatorio, como el número; en la entrega se ignora.</summary>
    string? IdentificationType = null,
    /// <summary>ISO alfa-2. <c>CO</c> es Colombia y la ciudad va por <c>DepartmentId</c>/<c>CityId</c>; otro país lleva
    /// <c>CityName</c> y ningún id (<c>quotations.party.city_lane_invalid</c> si se mezclan). En la entrega, ausente es
    /// Colombia. Al guardar, en la facturación con nombre propio es obligatorio (spec 2026-11-27): 422 en
    /// <c>Parties.Billing.Country</c>; el cálculo previo no lo exige.</summary>
    string? Country = null,
    /// <summary>La ciudad como texto (≤ 120), sólo fuera de Colombia.</summary>
    string? CityName = null,
    /// <summary>The region (province) of a party in a country with a region catalogue other than Colombia; replace
    /// semantics, like <c>CityName</c>: omitting it clears the stored one. With Colombia it is a 422.</summary>
    Guid? RegionId = null,
    /// <summary>The postal code of the delivery party (≤ 20, trimmed, no per-country format), in Colombia and abroad
    /// (spec 2026-11-24). Replace semantics, like <c>RegionId</c>: omitting it clears the stored one. Ignored on
    /// billing. Over the limit it is a 422 <c>quotations.party.postal_code_too_long</c> on
    /// <c>Parties.Shipping.PostalCode</c>.</summary>
    string? PostalCode = null);

/// <summary>Las dos partes de la cotización en el request. <b>Null es el caso normal</b>: "factura
/// (o entrega) a los datos del cliente" — el switch prendido de la UI. Como
/// <c>SaveQuotationRequest</c> reemplaza el recurso entero, mandar null en una parte que tenía
/// datos propios los borra y vuelve a los del cliente.</summary>
public sealed record QuotationPartiesRequest(
    QuotationPartyRequest? Billing,
    QuotationPartyRequest? Shipping,
    /// <summary>Con los datos del cliente, a cual de sus dos nombres se le factura: el de
    /// contacto (false, el default) o la razon social (true). Se ignora cuando <c>Billing</c>
    /// trae datos propios.</summary>
    bool BillingUsesBusinessName = false,
    /// <summary>El cliente recoge en la tienda (true) o se le entrega (false, el default: un
    /// frontend que todavia no manda el campo sigue cotizando con entrega). Gana sobre
    /// <c>Shipping</c>: con true, una parte de entrega que venga igual se descarta y la que
    /// estuviera guardada se borra.</summary>
    bool IsStorePickup = false,
    /// <summary>La factura sale a nombre de consumidor final (true) o a los datos del cliente o a
    /// <c>Billing</c> (false, el default: un frontend que todavia no manda el campo sigue
    /// facturando como antes). Con true, <c>Billing</c> tiene que venir null y
    /// <c>BillingUsesBusinessName</c> false; si no, 422
    /// <c>quotation.billing.final_consumer_conflict</c>. Como el guardado reemplaza el encabezado
    /// entero, omitirlo lo apaga.</summary>
    bool BillsToFinalConsumer = false,
    /// <summary>Se ignora (spec impuestos por tipo §9.3): lo manda el frontend viejo y la cotización ya no lo lee. Las
    /// retenciones de la facturación propia son <see cref="BillingWithholdingTaxRateIds"/>. Se queda en el record
    /// sólo para que el cuerpo del frontend viejo siga deserializando.</summary>
    bool? BillingWithRetention = null,
    /// <summary>Con <c>Billing</c> propio, si esa facturación tiene excedente de IVA. Null es "todavía no se
    /// contestó" y deja la cotización inconvertible a pedido (<c>quotation.billing.tax_profile_required</c>).</summary>
    bool? BillingVatSurplus = null,
    /// <summary>Los selectores de retención de la facturación propia (spec impuestos por tipo §7.5, §9.3): ids de
    /// tasas activas, que retienen, del país de la empresa facturadora, una por tipo. Null o ausente conserva los
    /// guardados (así el frontend viejo no los borra); una lista vacía los vacía. Con los datos del cliente se
    /// ignora.</summary>
    IReadOnlyList<Guid>? BillingWithholdingTaxRateIds = null);

/// <summary>
/// El cuerpo de <c>POST /quotations</c> y de <c>POST /quotations/preview</c> (spec 2026-10-18 §7.2). Crear y calcular
/// sin id existen para que "Nueva cotización" pinte el documento entero antes de que exista; sin esto la pantalla
/// tiene que crear un borrador para poder calcularlo. <c>Items</c> es la lista completa de líneas con producto y
/// cantidad; precio, escala, descuentos y tasa los resuelve el backend. Ausentes, los tres campos dan lo de antes:
/// sin líneas, sin piso, no detal.
/// </summary>
// Not sealed: PreviewNewQuotationRequest extends it with the preview-only billingCompanyId.
public record CreateQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    /// <summary>La entrega por envío; null es "sin envío". Ver <see cref="ShippingRequest"/>.</summary>
    ShippingRequest? Shipping = null,
    /// <summary>Las líneas con variante y cantidad, igual que en el guardado. Null o ausente es "sin líneas".</summary>
    IReadOnlyList<QuotationEditItemRequest>? Items = null,
    /// <summary>El piso de escala global; null es "sin piso".</summary>
    int? GlobalScaleFloor = null,
    /// <summary>Cotización detal: ninguna línea recibe descuento. Con un piso no nulo es 422
    /// <c>quotation.retail.floor_not_allowed</c>.</summary>
    bool IsRetail = false,
    /// <summary>El porcentaje de la retención municipal de cada tipo (spec ReteICA por empresa §7.7); ausente = el del
    /// catálogo. En la creación se valida contra la empresa y el municipio de este mismo cuerpo (§7.6, «En la creación»).</summary>
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    /// <summary>Los tipos municipales con «Sin retención» elegido (owner 2026-11-13); ausente, null o vacío = sin marca.</summary>
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    /// <summary><c>Standard</c> o <c>Project</c> (spec 2026-11-26 §6.5 y §12-G); ausente = <c>Standard</c>. No cambia
    /// después de crear.</summary>
    string? Kind = null,
    /// <summary>El alcance y el precio de un proyecto; sólo con <c>kind: "Project"</c>. Ausente nace vacío.</summary>
    ProjectScopeRequest? Project = null)
{
    /// <summary>
    /// El municipio de la operación (plan P1): ausente = el de la empresa, <c>null</c> = el de la empresa, un id = ése. Un
    /// <see cref="JsonElement"/> porque el contrato distingue ausente de null (spec §9.4), y propiedad y no parámetro del
    /// constructor porque el exportador de OpenAPI responde 500 con un <c>JsonElement</c> por defecto (precedente
    /// <c>UpdateTaxRateRequest.MinimumBase</c>). No se escribe cuando es el por defecto, así un cliente .NET que serializa
    /// el record sin tocarlo no revienta.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement OperationCityId { get; init; }
}

/// <summary>
/// The body of <c>POST /quotations/preview</c>: the creation body plus <see cref="BillingCompanyId"/> (spec 2026-11-16
/// withholdings by company). Its own type so <c>POST /quotations</c> never learns the field: creating keeps requiring
/// the full account to have one.
/// </summary>
public sealed record PreviewNewQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    ShippingRequest? Shipping = null,
    IReadOnlyList<QuotationEditItemRequest>? Items = null,
    int? GlobalScaleFloor = null,
    bool IsRetail = false,
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    /// <summary>The billing company chosen before its bank account: the preview paints its withholdings and answers
    /// <c>billingAccount: null</c>. Ignored when <c>billingAccount</c> is present.</summary>
    Guid? BillingCompanyId = null,
    string? Kind = null,
    ProjectScopeRequest? Project = null)
    : CreateQuotationRequest(
        ClientId, ValidUntil, PaymentMethod, Notes, Parties, BillingAccount, Shipping, Items, GlobalScaleFloor, IsRetail,
        WithholdingPercentages, ClearedWithholdingTypes, Kind, Project);

/// <summary>
/// La entrega por envío de una cotización tal como la pide el editor (spec 2026-10-17 §5.1). Viaja aparte de
/// <see cref="QuotationPartiesRequest"/> porque ese record también lo usa el borrador de un pedido externo, que
/// no recibe envío en este spec.
///
/// <b>Null en el cuerpo es "no lo toques"</b>, a diferencia del resto del encabezado: un frontend que todavía no
/// lo manda no borra el envío guardado. La excepción es pasar a recoger en tienda, que lo limpia igual.
/// </summary>
/// <param name="CarrierId">La transportadora del tenant, o null para ninguna. Independiente del modo.</param>
/// <param name="FreightChargeMode">El nombre del enum: <c>Charged</c>, <c>OnDelivery</c> o <c>Free</c>; null es
/// "sin envío registrado".</param>
/// <param name="FreightAmount">El valor con IVA incluido. Con <c>Free</c> se ignora y queda en 0.</param>
/// <param name="CashOnDelivery">Si el pedido se cobra contraentrega. No exige transportadora.</param>
public sealed record ShippingRequest(
    Guid? CarrierId,
    string? FreightChargeMode,
    decimal FreightAmount,
    bool CashOnDelivery);

/// <summary>
/// La entrega por envío de un documento, con forma fija también en recogida (spec 2026-10-17 §7).
///
/// BFF: la pantalla dibuja la tarjeta de entrega y la fila de totales sin recalcular nada. Por eso viajan
/// resueltos el nombre de la transportadora (la foto, no el de hoy), el subtotal y el IVA del envío, y
/// <see cref="IsStorePickup"/> repetido acá aunque ya esté en el documento: la tarjeta lee un solo objeto.
/// </summary>
/// <param name="Carrier">Null en recogida, sin transportadora y en documentos anteriores al envío.</param>
/// <param name="ChargeMode">El nombre del enum (<c>Charged</c>, <c>OnDelivery</c>, <c>Free</c>): el diccionario
/// lo tiene el frontend. Null es "sin envío registrado".</param>
/// <param name="FreightAmount">El valor con IVA incluido que escribió el asesor; con <c>OnDelivery</c> es
/// informativo y no suma.</param>
/// <param name="FreightTaxPercentage">La foto de la tasa del envío al fijar el valor; 0 fuera de COP.</param>
public sealed record ShippingDto(
    bool IsStorePickup,
    ShippingCarrierDto? Carrier,
    string? ChargeMode,
    decimal FreightAmount,
    int FreightTaxPercentage,
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    bool CashOnDelivery);

/// <summary>La transportadora elegida: el id y la foto del nombre al elegirla.</summary>
public sealed record ShippingCarrierDto(Guid Id, string Name);

/// <summary>Gemelo HTTP de <see cref="ShippingDto"/> —mismos campos, mismo significado— por la regla de este
/// archivo: ningún <c>*Response</c> referencia un <c>*Dto</c> (ver <see cref="QuotationMinimumPurchaseResponse"/>).
/// El motivo BFF de su forma está en <see cref="ShippingDto"/>.</summary>
public sealed record ShippingResponse(
    bool IsStorePickup,
    ShippingCarrierResponse? Carrier,
    string? ChargeMode,
    decimal FreightAmount,
    int FreightTaxPercentage,
    decimal FreightSubtotal,
    decimal FreightTaxAmount,
    bool CashOnDelivery)
{
    /// <summary>Copia campo por campo. Pública porque la fila del listado se arma en el proyecto Api.</summary>
    public static ShippingResponse From(ShippingDto shipping) => new(
        shipping.IsStorePickup,
        shipping.Carrier is { } carrier ? new ShippingCarrierResponse(carrier.Id, carrier.Name) : null,
        shipping.ChargeMode,
        shipping.FreightAmount,
        shipping.FreightTaxPercentage,
        shipping.FreightSubtotal,
        shipping.FreightTaxAmount,
        shipping.CashOnDelivery);
}

public sealed record ShippingCarrierResponse(Guid Id, string Name);

/// <summary>
/// US-2 (revisada): cambiar el cliente de una cotización editable. Endpoint propio y no un campo
/// más del guardado del encabezado porque arrastra consecuencias que el resto de la edición no
/// tiene —las partes de facturación y envío se borran, los totales se recalculan— y merece su
/// propia entrada de auditoría.
/// </summary>
public sealed record ChangeQuotationClientRequest(Guid ClientId);

/// <summary>Agrega una línea. <c>variantId</c> y no <c>productId</c> (spec 2026-10-20 §4): para un
/// producto sin opciones, el id de su variante por defecto.</summary>
public sealed record AddQuotationItemRequest(Guid VariantId, decimal Quantity);


public sealed record UpdateQuotationItemRequest(decimal Quantity);

/// <summary>Una línea a agregar, tal como viaja en el request de la tanda — mismo par que
/// <see cref="AddQuotationItemRequest"/>.</summary>
public sealed record BatchQuotationItemAdditionRequest(Guid VariantId, decimal Quantity);

/// <summary>El "Guardar" de la modal de agregar productos: varias altas y bajas de una sola
/// vez. Ver <see cref="BatchUpdateQuotationItemsCommand"/>.</summary>
public sealed record BatchUpdateQuotationItemsRequest(
    IReadOnlyList<BatchQuotationItemAdditionRequest> ToAdd,
    IReadOnlyList<Guid> ToRemoveItemIds);

/// <summary>Una línea del estado deseado de la cotización. Se direcciona por <c>variantId</c> (spec
/// 2026-10-20 §4; antes <c>productId</c>) y no por <c>itemId</c>: la lista es el estado deseado, y el
/// id de línea sólo lo conoce quien ya la tiene guardada. Dos tallas del mismo producto son dos
/// líneas.</summary>
public sealed record QuotationEditItemRequest(Guid VariantId, decimal Quantity);

/// <summary>
/// El estado deseado completo de una cotización editable: mismo cuerpo para
/// <c>PUT /quotations/{quotationId}</c> y <c>POST /quotations/{quotationId}/preview</c>.
///
/// El encabezado se reemplaza entero: lo que no
/// viene se limpia. <c>Items</c> es la lista **completa** de líneas deseadas, no un delta —
/// ausente o null equivale a vacía, o sea "sin productos".
/// </summary>
// Not sealed: PreviewQuotationRequest extends it with the preview-only billingCompanyId.
public record SaveQuotationRequest(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<QuotationEditItemRequest>? Items,
    /// <summary>El piso de escala global, o null para quitarlo. Ausente se lee como null: el
    /// guardado reemplaza el encabezado entero.</summary>
    int? GlobalScaleFloor = null,
    /// <summary>Cotización detal: con true ninguna línea recibe descuento. Ausente se lee como
    /// false, mismo criterio que el piso. Se aplica antes que el piso: true con un piso no nulo
    /// en el mismo cuerpo es 422 quotation.retail.floor_not_allowed.</summary>
    bool IsRetail = false,
    /// <summary>La entrega por envío. <b>Ausente deja la guardada</b> (un frontend viejo no la borra), salvo
    /// que la entrega pase a recoger en tienda: entonces se limpia. Ver <see cref="ShippingRequest"/>.</summary>
    ShippingRequest? Shipping = null,
    /// <summary>El porcentaje de la retención municipal de cada tipo (spec ReteICA por empresa §7.7). Ausente conserva
    /// todos; una entrada con <c>percentage: null</c> vuelve al del catálogo.</summary>
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    /// <summary>Los tipos municipales con «Sin retención» elegido (owner 2026-11-13): quitan la fila de ese tipo de los
    /// selectores de facturación propia. Ausente, null o vacío = sin marca.</summary>
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    /// <summary>El alcance y el precio de una cotización de proyecto: reemplaza el bloque entero y <b>ausente conserva
    /// el guardado</b> (spec 2026-11-26 §6.5). En una <c>Standard</c> es 422 <c>validation.failed</c>.</summary>
    ProjectScopeRequest? Project = null)
{
    /// <summary>
    /// El municipio de la operación (plan P1): ausente conserva el guardado (o el de la empresa si cambia, A6),
    /// <c>null</c> = el de la empresa, un id = ése. Ver <see cref="CreateQuotationRequest.OperationCityId"/> por qué es
    /// un <see cref="JsonElement"/> y una propiedad.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement OperationCityId { get; init; }
}

/// <summary>
/// The body of <c>POST /quotations/{quotationId}/preview</c>: the save body plus <see cref="BillingCompanyId"/> (spec
/// 2026-11-16 withholdings by company). Its own type so <c>PUT /quotations/{quotationId}</c> never learns the field: the
/// save keeps requiring the full account.
/// </summary>
public sealed record PreviewQuotationRequest(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<QuotationEditItemRequest>? Items,
    int? GlobalScaleFloor = null,
    bool IsRetail = false,
    ShippingRequest? Shipping = null,
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages = null,
    IReadOnlyList<string>? ClearedWithholdingTypes = null,
    /// <summary>The billing company chosen before its bank account: the preview paints its withholdings and answers
    /// <c>billingAccount: null</c>. Ignored when <c>billingAccount</c> is present.</summary>
    Guid? BillingCompanyId = null,
    ProjectScopeRequest? Project = null)
    : SaveQuotationRequest(
        ValidUntil, PaymentMethod, Notes, Parties, BillingAccount, Items, GlobalScaleFloor, IsRetail, Shipping,
        WithholdingPercentages, ClearedWithholdingTypes, Project);

/// <summary>US-12: el PDF ya se subió a Storage (flujo de carga firmada ya existente) antes de
/// esta llamada; acá sólo se referencia el archivo resultante.</summary>
/// <summary>
/// <c>PdfFileId</c> queda por compatibilidad y **se ignora**: el PDF lo genera el backend.
/// El frontend todavía lo manda, y hacerlo obligatorio -- o rechazarlo -- rompería el envío
/// en cuanto esto se despliegue. Se elimina cuando el frontend deje de armar el documento.
/// </summary>
/// <summary>
/// <c>Recipient</c> elige a quien se le manda el documento: <c>"Customer"</c> (el default) o
/// <c>"Billing"</c>, el telefono que esta cotizacion guardo como datos propios de facturacion.
/// Opcion cerrada y no un telefono: el numero lo resuelve el backend contra lo que ya tiene
/// guardado.
///
/// <b>El cuerpo entero es opcional</b>: el frontend manda <c>POST .../send</c> sin nada cuando no
/// hay a quien elegir. Ver <c>SendWorksWithoutABody</c> — un cuerpo requerido aca rompio el envio
/// con 500.
///
/// <c>ConnectionId</c> (spec 2026-10-21 §4.2) es la conexión de WhatsApp por la que sale, y sólo hace falta cuando el
/// tenant tiene varias disponibles y todavía no eligió: esa primera elección se guarda con el envío. Con una elección
/// ya guardada, mandar otra es <c>channel_already_set</c>; el cambio se hace desde Configuración.
///
/// Spec 2026-11-10 §4.6: <c>Recipient: "Contact"</c> manda al contacto <c>ContactId</c> de la empresa de la cotización.
/// <c>ThreadConnectionId</c> es la conexión del hilo desde el que se manda: no pasa por la elección guardada ni la cambia, y
/// sólo se considera con <c>Contact</c>.
/// </summary>
public sealed record SendQuotationRequest(
    Guid? PdfFileId = null,
    string? Recipient = null,
    Guid? ConnectionId = null,
    Guid? ContactId = null,
    Guid? ThreadConnectionId = null);

/// <summary>El cliente tal como lo muestra la pantalla de la cotización, con su libreta de
/// direcciones. Viaja acá para que el detalle y el editor no pidan la ficha completa a
/// Customers en una segunda consulta.</summary>
public sealed record QuotationClientResponse(
    Guid Id,
    string Cuc,
    string Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? CityId,
    string? CityName,
    Guid? DepartmentId,
    string? DepartmentName,
    /// <summary>La razón social, cuando el cliente es una empresa. Null si no lo es.</summary>
    string? BusinessName,
    bool WithRetention,
    bool VatSurplus,
    bool IsActive,
    /// <summary>Última edición de la ficha. La pantalla la usa para decir desde cuándo un
    /// cliente está inactivo.</summary>
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<QuotationClientAddressResponse> Addresses,
    /// <summary>País del cliente (ISO 3166-1 alfa-2), el que decide si a quien se factura aplica la regla
    /// colombiana (spec 2026-10-26). Null cuando quien arma la referencia no lo trae.</summary>
    string? Country = null,
    /// <summary>"Natural" o "Juridica", tal como está en la ficha del cliente. Null si no lo tiene.</summary>
    string? PersonType = null,
    /// <summary>Uno de los siete códigos de documento (NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID) de la ficha del cliente. Null si no lo tiene.</summary>
    string? IdentificationType = null,
    /// <summary>The region of a foreign customer, resolved to its name; null when it has none (provincia guardada).</summary>
    QuotationRegionDto? Region = null,
    /// <summary>Los perfiles de facturación del cliente (spec 2026-11-24 §3.6), la principal primero. La pantalla copia uno
    /// en la facturación del documento sin pedir la ficha (BFF). <c>null</c> se lee como vacío.</summary>
    IReadOnlyList<QuotationClientBillingProfileResponse>? BillingProfiles = null,
    /// <summary>Cuántos perfiles admite un cliente: viaja para que la pantalla no ofrezca guardar uno más sin escribir el
    /// número en el frontend (BFF).</summary>
    int BillingProfileLimit = 0,
    /// <summary>El número del documento de la ficha, sin el tipo (que viaja en <c>IdentificationType</c>): el detalle del
    /// pedido lo muestra cuando se factura con los datos del cliente. Null si la ficha no lo tiene.</summary>
    string? IdentificationNumber = null)
{
    /// <summary>Nunca <c>null</c>.</summary>
    public IReadOnlyList<QuotationClientBillingProfileResponse> BillingProfiles { get; init; } = BillingProfiles ?? [];
}

/// <summary>
/// Un perfil de facturación en el bloque <c>client</c>, con la misma forma que en la ficha del cliente (BFF: el frontend
/// usa el mismo parser en las dos pantallas).
/// </summary>
public sealed record QuotationClientBillingProfileResponse(
    Guid Id,
    string Name,
    string? PersonType,
    string IdentificationType,
    string IdentificationNumber,
    string? Phone,
    string? Email,
    string Address,
    string Country,
    Guid? CityId,
    string? CityName,
    Guid? DepartmentId,
    string? DepartmentName,
    QuotationRegionDto? Region,
    IReadOnlyList<QuotationClientWithholdingRateResponse> WithholdingTaxRates,
    bool VatSurplus,
    bool IsPrincipal);

/// <summary>
/// Una tasa de un perfil de facturación, con la forma de la tasa de la ficha del cliente y no la de la tasa de un documento
/// (<see cref="WithholdingTaxRateResponse"/>): trae <c>IsActive</c> para que la pantalla descarte la desactivada al copiar
/// el perfil.
/// </summary>
public sealed record QuotationClientWithholdingRateResponse(
    Guid Id,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    bool IsActive,
    string AppliesTo);

public sealed record QuotationClientAddressResponse(
    Guid Id,
    string Name,
    string Address,
    string? Phone,
    Guid? CityId,
    string? CityName,
    Guid? DepartmentId,
    string? DepartmentName,
    bool IsPrincipal,
    /// <summary>ISO alfa-2 de la dirección. Fuera de Colombia no hay ids de DIVIPOLA y la ciudad es el texto.</summary>
    string Country = "CO",
    /// <summary>The region of a foreign address, resolved to its name; null when it has none.</summary>
    QuotationRegionDto? Region = null);

/// <summary>
/// La escala viaja con su restricción, no sólo con su descuento: es lo único con lo que el
/// formulario puede evitar el 422 de <c>quotation.item.quantity_not_multiple</c> antes de
/// enviar, en vez de sólo reaccionar a él. <c>Restriction</c> es texto
/// ("multiple" | "packaging_unit") y no el enum, mismo criterio que
/// <c>PriceScaleResponse.Restriction</c> en Catalog — null incluido, para la escala incompleta.
/// </summary>
/// <param name="PackagingUnits">Los empaques del producto cuando la restricción es
/// <c>packaging_unit</c>; vacío en las demás, nunca null. Viajan en la escala aunque sean del
/// producto porque es la escala la que la pantalla tiene en la mano al explicar por qué una
/// cantidad no descuenta, y con dos empaques (100 y 150) el "múltiplo de N" ya no alcanza.</param>
public sealed record QuotationItemPriceScaleResponse(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string? Restriction,
    int? Multiple,
    IReadOnlyList<int> PackagingUnits);

public sealed record QuotationResponse(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    /// <summary>Null sólo si el cliente ya no existe: `ClientId` es una referencia blanda entre
    /// módulos y una cotización histórica tiene que poder leerse igual.</summary>
    QuotationClientResponse? Client,
    Guid AdvisorId,
    string? AdvisorEmail,
    /// <summary>El nombre que el tenant cargó en la membresía de la asesora. Null en membresías
    /// anteriores al nombre y en el owner hasta que lo cargue desde el roster. Hoy sólo lo usa el
    /// PDF, que cae a <c>AdvisorEmail</c> cuando falta (spec 2026-09-11, D6); la pantalla del
    /// detalle sigue mostrando el correo (D1). Aditivo: un front que no lo lee no se entera.</summary>
    string? AdvisorName,
    string Status,
    DateTimeOffset CreatedAt,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string Currency,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    // CustomerVatSurplus viaja para que el frontend pueda mostrar "exento por excedente de
    // IVA" en vez de adivinar por que TaxAmount dio cero. Es el valor **efectivo**
    // (Quotation.AppliesVatSurplus), no el snapshot del cliente: facturando a consumidor final
    // el IVA se cobra aunque el cliente tenga excedente, y quote-totals-summary.tsx pinta "IVA —
    // Exento" con solo ver esto en true. RetentionAmount/NetTotal son el snapshot de retencion en
    // la fuente (Quotation.RecalculateTotals): NetTotal = Total - RetentionAmount es lo que
    // efectivamente se cobra en efectivo.
    bool CustomerVatSurplus,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    IReadOnlyCollection<QuotationPartyResponse> Parties,
    bool BillingUsesBusinessName,
    // Mismo criterio que QuotationDto.BillingWithRetention/BillingVatSurplus: sólo tienen sentido
    // con Parties trayendo fila de facturación propia. BillingWithRetention es derivado de los
    // selectores y nunca null con parte propia; BillingVatSurplus en null es "todavía sin contestar".
    bool? BillingWithRetention,
    bool? BillingVatSurplus,
    // Viaja siempre, aunque sea false: la pantalla decide con esto si muestra el bloque de
    // entrega o "Recoger en tienda", y un campo ausente la obligaria a adivinar el default.
    bool IsStorePickup,
    // Viaja siempre por el mismo motivo: con esto la pantalla elige entre el bloque de
    // facturacion y la tarjeta fija de consumidor final. Nombre y NIT no viajan: son constantes
    // (FinalConsumer) que el frontend ya muestra antes de guardar.
    bool BillsToFinalConsumer,
    QuotationBillingResponse? BillingAccount,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    bool CanBeSent,
    bool HasChangesSinceSent,
    bool CanBeConvertedToOrder,
    /// <summary>Where the quotation stands against the minimum purchase. Computed by
    /// <c>QuotationResponseComposer</c> from the tenant settings, not carried by
    /// <c>QuotationDto</c> (plan decision A13).</summary>
    QuotationMinimumPurchaseResponse MinimumPurchase,
    IReadOnlyCollection<QuotationItemResponse> Items,
    /// <summary>Lo que la pantalla tiene que devolver en <c>If-Match</c> al guardar de una vez
    /// (<c>PUT /quotations/{quotationId}</c>). Viaja en **todas** las respuestas de cotización,
    /// no sólo en el GET: el frontend cachea lo que devuelve cada mutación, y una respuesta sin
    /// versión dejaría el próximo guardado sin con qué probar sobre qué está escribiendo. El
    /// mismo valor va además en el header <c>ETag</c> de la respuesta del PUT.</summary>
    long Version,
    /// <summary>El piso de escala global elegido para esta cotizacion, o null.</summary>
    int? GlobalScaleFloor,
    /// <summary>Cotizacion detal: ninguna linea recibe descuento, cualesquiera sean las escalas
    /// de su producto y la cantidad pedida. Es excluyente con <see cref="GlobalScaleFloor"/> —
    /// prenderlo lo deja en null, y con el prendido el endpoint de escala global rechaza
    /// cualquier piso no nulo. Viaja al lado suyo para que la pantalla lea los dos juntos.
    ///
    /// Llega solo a la pantalla de pedido: <c>OrderDetailResponse</c> devuelve este
    /// <see cref="QuotationResponse"/> entero.</summary>
    bool IsRetail,
    /// <summary>Los pisos entre los que el asesor puede elegir: los <c>FromUnit</c> distintos de
    /// las escalas de los productos que esta cotizacion tiene cargados, ordenados ascendente.
    /// **Completo incluso vacio** — una coleccion que desaparece obliga a la pantalla a
    /// reconstruirla desde las escalas linea por linea.</summary>
    IReadOnlyCollection<int> AvailableGlobalScaleFloors,
    /// <summary>La entrega por envío, con forma fija también en recogida. El motivo BFF está en
    /// <see cref="ShippingDto"/>.</summary>
    ShippingResponse Shipping,
    /// <summary>Envío sin IVA que suma a <c>Total</c>. Un total que no cierra se explica con un campo:
    /// <c>Subtotal + TaxAmount + FreightSubtotal + FreightTaxAmount = Total</c> (spec 2026-10-17 §5.3).</summary>
    decimal FreightSubtotal,
    /// <summary>IVA del envío que suma a <c>Total</c>.</summary>
    decimal FreightTaxAmount,
    /// <summary>Sólo las retenciones aplicadas, con su base y su monto; su suma es <c>RetentionAmount</c> (spec
    /// impuestos por tipo §9.3). Aditivo, como los dos de abajo.</summary>
    IReadOnlyList<WithholdingResponse> Withholdings,
    /// <summary>Las tasas del cliente congeladas en la cotización, apliquen o no.</summary>
    IReadOnlyList<WithholdingTaxRateResponse> CustomerWithholdingTaxRates,
    /// <summary>Los selectores de la facturación propia, apliquen o no: lo que prellena el editor.</summary>
    IReadOnlyList<WithholdingTaxRateResponse> BillingWithholdingTaxRates,
    /// <summary>Spec 2026-11-10 §4.6: los contactos sin fusionar de la empresa del cliente, para elegir a quién se envía el
    /// documento. Siempre presente: vacío si el cliente no tiene o si él mismo es un contacto. Lo llena el compositor.</summary>
    IReadOnlyList<ClientContactDto> ClientContacts,
    /// <summary>El municipio de la operación con nombre y departamento (spec ReteICA por empresa §9.4); null sin empresa,
    /// con una de un país sin tipos municipales o en un documento anterior (A8).</summary>
    QuotationCityResponse? OperationCity,
    /// <summary>El municipio de la empresa congelado con la cuenta (A12): es lo que «Usar el de la empresa» compara con
    /// <see cref="OperationCity"/>. Null sin empresa, fuera de CO o en un documento que todavía no reenvió la cuenta.</summary>
    QuotationCityResponse? BillingCompanyCity,
    /// <summary>Una entrada por tipo municipal del país de la empresa, con la tasa que la empresa tiene hoy ahí y por qué
    /// quedó o no una fila (A14). BFF: la pantalla no puede saber las asignaciones de la empresa ni la lista de agentes del
    /// cliente, así que el porqué se calcula acá y no en el navegador. Vacía sin empresa o sin tipos municipales.</summary>
    IReadOnlyList<MunicipalWithholdingResponse> MunicipalWithholdings,
    /// <summary>Todas las tasas de la fuente vigente con su veredicto, apliquen o no, en su orden (spec retenciones por UVT
    /// §6). Aditivo: <see cref="Withholdings"/> sigue trayendo sólo las aplicadas.</summary>
    IReadOnlyList<WithholdingBreakdownResponse> WithholdingBreakdown,
    /// <summary>Si la cotización vende algo que se despacha (spec 2026-11-18 §5): alguna línea <c>Physical</c>, o
    /// ninguna línea. BFF: con false la pantalla oculta la sección de envío sin conocer el tipo de cada línea ni la
    /// regla del documento vacío. El backend no pone ni borra el envío por esto. Con default para no tocar las
    /// construcciones que ya existen; el compositor lo llena siempre.</summary>
    bool RequiresShipping = true,
    /// <summary>This backend stores the delivery postal code (spec 2026-11-24). BFF: a new document has no party to look
    /// at, and the screen only draws the field, and only sends it, when the document says so; a backend without it would
    /// silently drop what the person typed. Always true.</summary>
    bool PostalCodeSupported = true,
    /// <summary>Ver <see cref="QuotationDto.Kind"/>. BFF: con <c>Project</c> la pantalla abre el editor de alcance en vez
    /// del de productos.</summary>
    string Kind = "Standard",
    /// <summary>Ver <see cref="ProjectScopeDto"/>; null en una <c>Standard</c>.</summary>
    ProjectScopeDto? Project = null);

/// <summary>Una referencia de municipio (spec ReteICA por empresa §9.1): la misma forma en tasas, empresas, clientes y
/// documentos.</summary>
public sealed record QuotationCityResponse(Guid Id, string Name, Guid DepartmentId, string DepartmentName);

/// <summary>La tasa municipal que la empresa tiene hoy para un tipo en el municipio de la operación: lo único que el
/// selector municipal acepta (A4).</summary>
public sealed record MunicipalCompanyRateResponse(Guid TaxRateId, string Name, decimal Percentage);

/// <summary>
/// El porqué de un tipo municipal en un documento (spec ReteICA por empresa §9.4). Se deriva al leer, con las asignaciones
/// de la empresa y los agentes del cliente de hoy (A14): cambiarlos cambia esto, nunca la foto ni los totales.
/// </summary>
/// <param name="Status">El nombre de <c>MunicipalWithholdingStatus</c>: <c>Resolved</c>, <c>NoOperationCity</c>,
/// <c>NoCompanyRate</c>, <c>CustomerNotAgent</c> o <c>NotSelected</c>.</param>
/// <param name="CustomerIsAgent">Si el cliente está hoy registrado como agente de ese tipo en el municipio; null con
/// facturación propia (el tercero no tiene lista) o sin municipio. <c>Resolved</c> con false es una tasa aplicada a mano
/// (R11), y el frontend lo explica así.</param>
public sealed record MunicipalWithholdingResponse(
    string Type,
    string Country,
    MunicipalCompanyRateResponse? CompanyRate,
    string Status,
    bool? CustomerIsAgent);

/// <summary>Un contacto de la empresa del cliente tal como lo ve el diálogo de envío (spec 2026-11-10 §4.6). El teléfono es el de su
/// ficha y puede faltar: ese contacto se muestra pero no se puede elegir.</summary>
public sealed record ClientContactDto(Guid Id, string Name, string? Phone);

/// <summary>Gemelo HTTP de <see cref="WithholdingDto"/>, por la regla de pares de este archivo (ver
/// <see cref="QuotationMinimumPurchaseResponse"/>).</summary>
public sealed record WithholdingResponse(
    Guid TaxRateId,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal? MinimumBase,
    string? MinimumBaseUnit,
    decimal? TaxUnitValue,
    decimal Base,
    decimal Amount,
    decimal CatalogPercentage,
    bool PercentageOverridden,
    Guid? CityId,
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <summary>Gemelo HTTP de <see cref="WithholdingTaxRateDto"/>; <c>AppliesTo</c> significa lo mismo que allá.</summary>
public sealed record WithholdingTaxRateResponse(
    Guid Id,
    string Name,
    string Type,
    string Country,
    decimal Percentage,
    decimal CatalogPercentage,
    bool PercentageOverridden,
    Guid? CityId,
    string AppliesTo = nameof(BuildingBlocks.Domain.Taxes.WithholdingScope.All));

/// <summary>
/// La compra mínima tal como viaja por HTTP. Es un gemelo de
/// <see cref="QuotationMinimumPurchaseDto"/> —mismos campos, mismo significado— y no el mismo
/// record a propósito: ningún <c>*Response</c> de este archivo referencia un <c>*Dto</c>.
///
/// Esa separación es el seam que defiende <c>QuotationResponseComposer</c>: el contrato HTTP se
/// arma a mano ahí, así que agregar un campo al DTO interno no lo publica solo. El costo de la
/// regla es este archivo con pares; el beneficio es que nadie cambia lo que ve el navegador sin
/// tocar el contrato. Ver <see cref="QuotationMinimumPurchaseDto"/> para qué significa cada campo
/// y por qué el mensaje es prospectivo.
/// </summary>
public sealed record QuotationMinimumPurchaseResponse(
    bool Met,
    decimal Units,
    decimal MinimumUnits,
    /// <summary>The quotation currency, so the screen formats both totals without asking.</summary>
    string Currency,
    /// <summary>Null when the tenant has no minimum total for this currency: the gate only opens by units (spec D7).</summary>
    decimal? MinimumTotal,
    decimal MissingUnits,
    /// <summary>Null when the tenant has no minimum total for this currency: the gate only opens by units (spec D7).</summary>
    decimal? MissingTotal);

/// <summary>
/// La cuenta con la que se factura, ya resuelta para la pantalla: la copia guardada más la razón
/// social y el NIT que la empresa tiene <b>hoy</b>. Los dos últimos son null si la empresa se
/// borró — el id es una referencia blanda entre módulos y la cotización tiene que poder leerse
/// igual, mismo criterio que el bloque de cliente.
/// </summary>
public sealed record QuotationBillingResponse(
    Guid CompanyId,
    string? CompanyName,
    string? CompanyTaxId,
    string? CompanyDocumentType,
    /// <summary>Membrete del PDF, no congelado: ver <see cref="QuotationCompanyRef"/>.</summary>
    string? CompanyAddress,
    string? CompanyPhone,
    /// <summary>Null, junto con <c>AccountNumber</c>, en una empresa sin cuenta (spec 2026-11-17 §4.2).</summary>
    string? BankName,
    string? AccountNumber,
    string Currency,
    /// <summary>Si la empresa emisora cobra IVA, congelado con el documento (spec 2026-11-17 §6).</summary>
    bool ChargesVat = true);

public sealed record QuotationPartyResponse(
    Guid Id,
    string Role,
    string? Name,
    string? IdentificationNumber,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId,
    string? PersonType = null,
    string? IdentificationType = null,
    string? Country = null,
    string? CityName = null,
    QuotationRegionDto? Region = null,
    /// <summary>Only on the shipping party; always null on billing.</summary>
    string? PostalCode = null);

public sealed record QuotationListItemResponse(
    Guid Id,
    string QuotationNumber,
    Guid ClientId,
    string? ClientName,
    Guid AdvisorId,
    /// <summary>El nombre de la asesora, o su correo si la membresía no tiene nombre. Viaja ya
    /// resuelto para que la grilla pinte un solo campo; el porqué está en
    /// <see cref="QuotationListItemDto.AdvisorName"/>.</summary>
    string? AdvisorName,
    string Status,
    DateTimeOffset CreatedAt,
    /// <summary>La moneda de <c>Total</c>. Viaja por fila porque la grilla mezcla cotizaciones
    /// en pesos y en dolares, y una columna de importes sin moneda seria ilegible.</summary>
    string Currency,
    decimal Total,
    /// <summary>Si el envio tiene sentido ahora. Mismo campo y mismo significado que en
    /// <see cref="QuotationResponse"/>.</summary>
    bool CanBeSent,
    /// <summary>Si ya es un documento presentable: al menos una linea, vigencia y cuenta de
    /// cobro.</summary>
    bool IsComplete,
    /// <summary>El pedido que salió de esta cotización. <c>null</c> es "sin convertir".</summary>
    Guid? OrderId,
    /// <summary><c>Pending</c> o <c>Approved</c>; <c>null</c> sin pedido.</summary>
    string? OrderStatus,
    /// <summary>La entrega por envío de la fila, misma forma que en el detalle: la grilla muestra
    /// transportadora y contraentrega sin pedir la cotización entera.</summary>
    ShippingResponse Shipping,
    /// <summary>Mismo campo y mismo significado que en <see cref="QuotationResponse"/> (spec 2026-11-18 §5): la fila no
    /// trae las líneas, así que viaja resuelto.</summary>
    bool RequiresShipping,
    /// <summary>Ver <see cref="QuotationDto.Kind"/>.</summary>
    string Kind,
    /// <summary><c>Global</c> o <c>PerPhase</c>; null en una <c>Standard</c> (spec 2026-11-26 §6.5). BFF: es la columna
    /// «Modo» de la lista de proyectos, que no trae el alcance.</summary>
    string? PricingMode);

/// <summary>El sobre del historial. Colección envuelta y no un array desnudo, mismo criterio que
/// el resto de las colecciones de la API.</summary>
public sealed record QuotationHistoryResponse(
    IReadOnlyCollection<QuotationHistoryEntryDto> Items);

public sealed record QuotationsPageResponse(
    IReadOnlyCollection<QuotationListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Por qué el piso global no le dio su descuento a esta línea. <c>Reason</c> es un código y no
/// un texto: <c>packaging_unit</c> (no se arma con paquetes enteros de los empaques del producto;
/// <c>PackagingUnits</c> los trae), <c>multiple</c> (no es múltiplo; <c>Step</c> es el paso) o
/// <c>no_tier</c> (el producto no tiene un tramo que arranque en ese piso). La frase la arma el
/// frontend, que es el que tiene el diccionario. Ver <see cref="QuotationGlobalScaleFloorMiss"/>.
///
/// <c>Step</c> quedó sólo para <c>multiple</c>: con dos empaques (100 y 150) no hay un único
/// número que decir, y uno solo —el menor, por ejemplo— le haría creer al asesor que 150 no
/// sirve. Por eso <c>packaging_unit</c> lleva la lista, y <c>Step</c> en null.
/// </summary>
/// <param name="PackagingUnits">Vacío salvo en <c>packaging_unit</c>; nunca null.</param>
public sealed record GlobalScaleFloorMissResponse(
    string Reason, int? Step, IReadOnlyList<int> PackagingUnits);

public sealed record QuotationItemResponse(
    Guid Id,
    /// <summary>La variante que vende la línea (spec 2026-10-20 §4): es lo que la pantalla manda de
    /// vuelta como <c>variantId</c> al guardar la lista de líneas, así que viaja aunque el nombre ya
    /// la describa.</summary>
    Guid VariantId,
    /// <summary>El producto dueño de la variante. Viaja para que la pantalla pueda agrupar o enlazar
    /// al producto sin conocer la variante.</summary>
    Guid ProductId,
    /// <summary>Nombre visible, código, portada y escalas de la variante, resueltos por el backend
    /// (spec 2026-10-20 §3.4: «Camiseta – M», el <c>Code</c> de la variante, su foto o la principal
    /// del producto). Sin esto la pantalla tenía que traerse el catálogo entero para poner un nombre
    /// en cada línea. Vacíos si la variante ya no existe.</summary>
    string ProductName,
    string ProductCode,
    string? ProductImageUrl,
    IReadOnlyCollection<QuotationItemPriceScaleResponse> PriceScales,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    /// <summary>Lo que cuesta cada unidad ya con el descuento aplicado, IVA adentro — misma
    /// unidad que <c>UnitPrice</c>. <c>Subtotal</c> está en la otra: es la base <b>sin</b> IVA.
    /// Multiplicar éste por <c>Quantity</c> da lo que se cobra por la línea.</summary>
    decimal DiscountedUnitPrice,
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position,
    /// <summary>El nombre de <c>QuotationDiscountOrigin</c>: <c>Own</c>, <c>Group</c> o
    /// <c>GlobalFloor</c>. Mismo criterio que el resto de los enums, que viajan con su nombre
    /// porque el diccionario lo tiene el frontend.</summary>
    string DiscountOrigin,
    /// <summary>Por qué el piso global no le dio su descuento a esta línea, o null si no hay nada
    /// que explicar. Con default para no tocar las construcciones que ya existen.</summary>
    GlobalScaleFloorMissResponse? GlobalScaleFloorMiss = null,
    /// <summary>Producto o servicio (<c>Physical</c> | <c>Service</c>) y la unidad de venta (<c>Unit</c>, <c>Hour</c>…),
    /// por nombre (spec 2026-11-18 §3.4). Son los que la línea fijó al nacer, no los del catálogo de hoy: la pantalla
    /// pone la unidad junto a la cantidad con su propio diccionario. Con default por la misma razón que el anterior.</summary>
    string Kind = "Physical",
    string SalesUnit = "Unit",
    /// <summary>Mayorista fase 3: en un pedido mayorista, si la línea vino de la orden de compra del distribuidor (false = la
    /// agregó el mayorista). Null en cualquier otro documento.</summary>
    bool? FromPurchaseOrder = null,
    /// <summary>Regalo (spec 2026-11-21): la pantalla lo dibuja aparte y no puede deducirlo de un descuento del 100 %, que también
    /// puede escribirse a mano. Sólo los pedidos lo traen en true.</summary>
    bool IsGift = false);

/// <summary>
/// El 202 de las exportaciones por correo (spec 2026-09-12, D5), de cotizaciones y de pedidos. No
/// lleva nombre de archivo ni cantidad de filas porque todavía no existen, ni enlace porque el
/// canal de entrega es el correo: con el enlace acá, la pantalla tomaría el atajo y el correo
/// quedaría sin ejercitar. El jobId es para soporte y para una futura "mis exportaciones" (D15).
/// </summary>
public sealed record ExportJobAcceptedResponse(Guid JobId, DateTimeOffset RequestedAt);
