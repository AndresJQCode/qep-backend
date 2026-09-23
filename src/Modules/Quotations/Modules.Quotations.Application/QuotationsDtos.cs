namespace Modules.Quotations.Application;

public sealed record QuotationItemDto(
    Guid Id,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    /// <summary>Lo que cuesta cada unidad ya con el descuento aplicado, IVA adentro — misma
    /// unidad que <c>UnitPrice</c>, no la de <c>Subtotal</c>. Lo calcula la línea
    /// (<c>QuotationItem.DiscountedUnitPrice</c>) y no quien lo muestra: la pantalla y el PDF lo
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
    string DiscountOrigin);

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
    /// <summary>La moneda de todos los importes de abajo: "COP" o "USD". La fija la cuenta de
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
    /// <summary>Si quien recibe la factura practica retención en la fuente, sólo cuando
    /// <c>Parties</c> trae fila de facturación propia (<c>Quotation.PartyWithRetention</c>). Null
    /// con los datos del cliente (ahí manda <c>CustomerVatSurplus</c>/lo que ya diga el cliente) o
    /// mientras no se contestó con datos propios — la pantalla lo trata como "todavía sin elegir",
    /// no como false.</summary>
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
    /// <summary>En qué anda la cotización contra la compra mínima que habilita los descuentos por
    /// volumen. Viaja **siempre**, alcanzada o no.</summary>
    QuotationMinimumPurchaseDto MinimumPurchase,
    IReadOnlyCollection<QuotationItemDto> Items,
    /// <summary>La versión del agregado con la que se leyó, para mandarla en <c>If-Match</c> al
    /// guardar de una vez (<c>PUT /quotations/{quotationId}</c>). Ya existía en el dominio
    /// (<c>Quotation.Version</c>) y no salía de la aplicación: sin ella la pantalla no tiene con
    /// qué probar que está guardando sobre lo que leyó. Va al final por ser aditiva.</summary>
    long Version,
    /// <summary>El piso de escala global elegido, o null. Ver
    /// <c>Quotation.GlobalScaleFloor</c>. Con default para no tocar las construcciones que ya
    /// existen: solo QuotationMapping.ToDto lo llena.</summary>
    int? GlobalScaleFloor = null,
    /// <summary>Cotización detal: ninguna línea recibe descuento, cualesquiera sean las escalas
    /// de su producto. Ver <c>Quotation.IsRetail</c>. Con default por el mismo motivo que
    /// <see cref="GlobalScaleFloor"/>.</summary>
    bool IsRetail = false);

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
/// <param name="MinimumTotal">El total que habilita el descuento por sí solo, **en la moneda de
/// la cotización** (<c>Currency</c>). No es una conversión: este módulo no tiene tabla de cambio,
/// así que el mínimo en dólares es un número propio.</param>
/// <param name="MissingUnits">Cuántas unidades faltan para <paramref name="MinimumUnits"/>. 0
/// cuando <paramref name="Met"/>.</param>
/// <param name="MissingTotal">Cuánta plata falta para <paramref name="MinimumTotal"/>, contra el
/// total ya descontado y con IVA. 0 cuando <paramref name="Met"/>.</param>
public sealed record QuotationMinimumPurchaseDto(
    bool Met,
    decimal Units,
    decimal MinimumUnits,
    decimal MinimumTotal,
    decimal MissingUnits,
    decimal MissingTotal);

/// <summary>Una parte (facturación o entrega) tal como sale hacia el cliente HTTP. Role es texto
/// y no el enum del dominio, mismo criterio que Status.</summary>
public sealed record QuotationPartyDto(
    Guid Id,
    string Role,
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId);

/// <summary>La cuenta con la que se factura, tal como sale hacia el cliente HTTP. Es la copia
/// guardada, no lo que la empresa tenga hoy.</summary>
public sealed record QuotationBillingAccountDto(
    Guid CompanyId,
    string BankName,
    string AccountNumber,
    string Currency);

/// <summary>
/// La cuenta elegida, tal como viaja en el request. Llegan los cuatro campos y no sólo el id de
/// la empresa porque una <c>CompanyBankAccount</c> no tiene identidad propia: la terna
/// banco/número/moneda es lo único que la distingue dentro de su empresa. El handler la verifica
/// contra las cuentas de esa empresa antes de copiarla — el cuerpo lo escribe el cliente.
/// </summary>
public sealed record QuotationBillingAccountRequest(
    Guid CompanyId,
    string BankName,
    string AccountNumber,
    string Currency);

/// <summary>Los datos de una parte tal como viajan en el request (US-6). Cada campo null es
/// "para éste, el del cliente".</summary>
public sealed record QuotationPartyRequest(
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId);

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
    /// <summary>Sólo tiene sentido cuando <c>Billing</c> trae datos propios: si esa facturación
    /// practica retención en la fuente. Con los datos del cliente se ignora — ahí manda lo que
    /// diga el cliente. Null es "todavía no se contestó"; un <c>Billing</c> con datos propios y
    /// esto en null deja la cotización inconvertible a pedido
    /// (<c>quotation.billing.tax_profile_required</c>).</summary>
    bool? BillingWithRetention = null,
    /// <summary>Mismo criterio que <c>BillingWithRetention</c> pero para el excedente de IVA.</summary>
    bool? BillingVatSurplus = null);

public sealed record CreateQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount);

/// <summary>
/// US-2 (revisada): cambiar el cliente de una cotización editable. Endpoint propio y no un campo
/// más del guardado del encabezado porque arrastra consecuencias que el resto de la edición no
/// tiene —las partes de facturación y envío se borran, los totales se recalculan— y merece su
/// propia entrada de auditoría.
/// </summary>
public sealed record ChangeQuotationClientRequest(Guid ClientId);

public sealed record AddQuotationItemRequest(Guid ProductId, decimal Quantity);


/// <summary>
/// Prende o apaga la cotizacion detal. Ver <see cref="SetQuotationRetailCommand"/>: con el
/// prendido ninguna linea recibe descuento y el piso de escala global queda en null.
/// </summary>
public sealed record SetRetailRequest(bool IsRetail);

public sealed record UpdateQuotationItemRequest(decimal Quantity);

/// <summary>Una línea a agregar, tal como viaja en el request de la tanda — mismo par que
/// <see cref="AddQuotationItemRequest"/>.</summary>
public sealed record BatchQuotationItemAdditionRequest(Guid ProductId, decimal Quantity);

/// <summary>El "Guardar" de la modal de agregar productos: varias altas y bajas de una sola
/// vez. Ver <see cref="BatchUpdateQuotationItemsCommand"/>.</summary>
public sealed record BatchUpdateQuotationItemsRequest(
    IReadOnlyList<BatchQuotationItemAdditionRequest> ToAdd,
    IReadOnlyList<Guid> ToRemoveItemIds);

/// <summary>Una línea del estado deseado de la cotización. Se direcciona por <c>productId</c> y no
/// por <c>itemId</c>: la lista es el estado deseado, y el id de línea sólo lo conoce quien ya la
/// tiene guardada.</summary>
public sealed record QuotationEditItemRequest(Guid ProductId, decimal Quantity);

/// <summary>
/// El estado deseado completo de una cotización editable: mismo cuerpo para
/// <c>PUT /quotations/{quotationId}</c> y <c>POST /quotations/{quotationId}/preview</c>.
///
/// El encabezado se reemplaza entero: lo que no
/// viene se limpia. <c>Items</c> es la lista **completa** de líneas deseadas, no un delta —
/// ausente o null equivale a vacía, o sea "sin productos".
/// </summary>
public sealed record SaveQuotationRequest(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<QuotationEditItemRequest>? Items,
    /// <summary>El piso de escala global, o null para quitarlo. Ausente se lee como null: el
    /// guardado reemplaza el encabezado entero.</summary>
    int? GlobalScaleFloor = null);

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
/// </summary>
public sealed record SendQuotationRequest(
    Guid? PdfFileId = null,
    string? Recipient = null);

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
    IReadOnlyCollection<QuotationClientAddressResponse> Addresses);

public sealed record QuotationClientAddressResponse(
    Guid Id,
    string Name,
    string Address,
    string? Phone,
    Guid CityId,
    string CityName,
    Guid DepartmentId,
    string DepartmentName,
    bool IsPrincipal);

/// <summary>
/// La escala viaja con su restricción, no sólo con su descuento: es lo único con lo que el
/// formulario puede evitar el 422 de <c>quotation.item.quantity_not_multiple</c> antes de
/// enviar, en vez de sólo reaccionar a él. <c>Restriction</c> es texto
/// ("multiple" | "packaging_unit") y no el enum, mismo criterio que
/// <c>PriceScaleResponse.Restriction</c> en Catalog — null incluido, para la escala incompleta.
/// </summary>
public sealed record QuotationItemPriceScaleResponse(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string? Restriction,
    int? Multiple,
    int? PackagingUnit);

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
    // con Parties trayendo fila de facturación propia, y null ahí es "todavía sin contestar".
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
    /// <summary>En qué anda la cotización contra la compra mínima. Misma posición que en
    /// <see cref="QuotationDto.MinimumPurchase"/> para que las dos formas se lean en
    /// paralelo.</summary>
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
    IReadOnlyCollection<int> AvailableGlobalScaleFloors);

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
    decimal MinimumTotal,
    decimal MissingUnits,
    decimal MissingTotal);

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
    /// <summary>Membrete del PDF, no congelado: ver <see cref="QuotationCompanyRef"/>.</summary>
    string? CompanyAddress,
    string? CompanyPhone,
    string BankName,
    string AccountNumber,
    string Currency);

public sealed record QuotationPartyResponse(
    Guid Id,
    string Role,
    string? Name,
    string? Phone,
    string? Email,
    string? Address,
    Guid? DepartmentId,
    Guid? CityId);

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
    string? OrderStatus);

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
/// un texto: <c>packaging_unit</c> (no es un número entero de paquetes; <c>Step</c> es el tamaño
/// del paquete), <c>multiple</c> (no es múltiplo; <c>Step</c> es el paso) o <c>no_tier</c> (el
/// producto no tiene un tramo que arranque en ese piso). La frase la arma el frontend, que es
/// el que tiene el diccionario. Ver <see cref="QuotationGlobalScaleFloorMiss"/>.
/// </summary>
public sealed record GlobalScaleFloorMissResponse(string Reason, int? Step);

public sealed record QuotationItemResponse(
    Guid Id,
    Guid ProductId,
    /// <summary>Nombre, código, portada y escalas del producto, resueltos por el backend. Sin
    /// esto la pantalla tenía que traerse el catálogo entero para poner un nombre en cada
    /// línea. Vacíos si el producto ya no existe.</summary>
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
    GlobalScaleFloorMissResponse? GlobalScaleFloorMiss = null);

/// <summary>
/// El 202 de las exportaciones por correo (spec 2026-09-12, D5), de cotizaciones y de pedidos. No
/// lleva nombre de archivo ni cantidad de filas porque todavía no existen, ni enlace porque el
/// canal de entrega es el correo: con el enlace acá, la pantalla tomaría el atajo y el correo
/// quedaría sin ejercitar. El jobId es para soporte y para una futura "mis exportaciones" (D15).
/// </summary>
public sealed record ExportJobAcceptedResponse(Guid JobId, DateTimeOffset RequestedAt);
