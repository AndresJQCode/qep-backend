namespace Modules.Quotations.Application;

public sealed record QuotationItemDto(
    Guid Id,
    Guid ProductId,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position);

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
    // IVA" en vez de adivinar por que TaxAmount dio cero. RetentionAmount/NetTotal son el
    // snapshot de retencion en la fuente (Quotation.RecalculateTotals): NetTotal = Total -
    // RetentionAmount es lo que efectivamente se cobra en efectivo.
    bool CustomerVatSurplus,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    /// <summary>Sólo las partes que difieren del cliente. Una cotización que factura y entrega
    /// a los datos del cliente llega con la lista vacía.</summary>
    IReadOnlyCollection<QuotationPartyDto> Parties,
    /// <summary>Si la facturación sigue al cliente, si va con su razón social.</summary>
    bool BillingUsesBusinessName,
    /// <summary>Con qué empresa y a qué cuenta se cobra. Null mientras nadie la eligió.</summary>
    QuotationBillingAccountDto? BillingAccount,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    /// <summary>Si tiene sentido ofrecer "enviar" ahora: un borrador siempre, y una enviada
    /// sólo si volvió a cambiar desde entonces.</summary>
    bool CanBeSent,
    /// <summary>Si convertir en venta es posible: enviada, con productos, vigencia, forma de
    /// pago y cuenta de cobro.</summary>
    bool CanBeConvertedToSale,
    IReadOnlyCollection<QuotationItemDto> Items);

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
/// <c>UpdateQuotationRequest</c> reemplaza el recurso entero, mandar null en una parte que tenía
/// datos propios los borra y vuelve a los del cliente.</summary>
public sealed record QuotationPartiesRequest(
    QuotationPartyRequest? Billing,
    QuotationPartyRequest? Shipping,
    /// <summary>Con los datos del cliente, a cual de sus dos nombres se le factura: el de
    /// contacto (false, el default) o la razon social (true). Se ignora cuando <c>Billing</c>
    /// trae datos propios.</summary>
    bool BillingUsesBusinessName = false);

public sealed record CreateQuotationRequest(
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount);

public sealed record UpdateQuotationRequest(
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount);

/// <summary>
/// US-2 (revisada): cambiar el cliente de una cotización editable. Endpoint propio y no un campo
/// más del PATCH porque arrastra consecuencias que el resto de la edición no tiene —las partes de
/// facturación y envío se borran, los totales se recalculan— y merece su propia entrada de
/// auditoría.
/// </summary>
public sealed record ChangeQuotationClientRequest(Guid ClientId);

public sealed record AddQuotationItemRequest(Guid ProductId, decimal Quantity);

public sealed record UpdateQuotationItemRequest(decimal Quantity);

/// <summary>US-12: el PDF ya se subió a Storage (flujo de carga firmada ya existente) antes de
/// esta llamada; acá sólo se referencia el archivo resultante.</summary>
public sealed record SendQuotationRequest(Guid PdfFileId);

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
/// <c>PriceScaleResponse.Restriction</c> en Catalog.
/// </summary>
public sealed record QuotationItemPriceScaleResponse(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string Restriction,
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
    // IVA" en vez de adivinar por que TaxAmount dio cero. RetentionAmount/NetTotal son el
    // snapshot de retencion en la fuente (Quotation.RecalculateTotals): NetTotal = Total -
    // RetentionAmount es lo que efectivamente se cobra en efectivo.
    bool CustomerVatSurplus,
    decimal RetentionAmount,
    decimal NetTotal,
    string? Notes,
    IReadOnlyCollection<QuotationPartyResponse> Parties,
    bool BillingUsesBusinessName,
    QuotationBillingResponse? BillingAccount,
    Guid CreatedBy,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? SentAt,
    Guid? PdfFileId,
    bool CanBeSent,
    bool CanBeConvertedToSale,
    IReadOnlyCollection<QuotationItemResponse> Items);

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
    string? AdvisorEmail,
    string Status,
    DateTimeOffset CreatedAt,
    /// <summary>La moneda de <c>Total</c>. Viaja por fila porque la grilla mezcla cotizaciones
    /// en pesos y en dolares, y una columna de importes sin moneda seria ilegible.</summary>
    string Currency,
    decimal Total);

/// <summary>El sobre del historial. Colección envuelta y no un array desnudo, mismo criterio que
/// el resto de las colecciones de la API.</summary>
public sealed record QuotationHistoryResponse(
    IReadOnlyCollection<QuotationHistoryEntryDto> Items);

public sealed record QuotationsPageResponse(
    IReadOnlyCollection<QuotationListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);

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
    decimal Subtotal,
    int TaxPercentage,
    decimal TaxAmount,
    int Position);
