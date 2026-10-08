namespace Modules.Pos.Application;

// Respuestas BFF del punto de venta (spec 2026-10-07, «API»). Se diseñan por lo que la pantalla
// dibuja: los enums viajan por nombre (el diccionario de etiquetas lo tiene el frontend), las
// colecciones de tamaño fijo viajan completas incluso en cero, y las horas viajan ya en el huso
// del tenant porque la pantalla no lo conoce.

public sealed record PosCashierResponse(Guid MemberId, string Name);

public sealed record PosCompanyOption(Guid Id, string Name, string TaxId);

public sealed record PosCompanyHeader(string Name, string TaxId);

/// <summary>Los tres medios siempre, en cero si no hubo: la pantalla no conoce el enum.</summary>
public sealed record PosPaymentTotalResponse(string Method, decimal Amount);

/// <param name="OpenedBeforeToday">Fecha local de apertura &lt; hoy del tenant: la pantalla avisa de una caja que quedó abierta desde otro día.</param>
/// <param name="ExpectedCash">Vivo: base + efectivo neto.</param>
/// <param name="Version">La que el cierre manda en If-Match (1 al abrir, +1 por venta o anulación).</param>
/// <param name="Currency">La moneda congelada al abrir la caja, no la vigente del tenant.</param>
public sealed record PosOpenSessionResponse(
    Guid Id,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset OpenedAtLocal,
    bool OpenedBeforeToday,
    PosCompanyOption Company,
    decimal OpeningFloat,
    int SalesCount,
    int VoidedCount,
    decimal SalesTotal,
    decimal ExpectedCash,
    long Version,
    IReadOnlyList<PosPaymentTotalResponse> PaymentTotals,
    string Currency);

/// <param name="Session">null sin caja abierta: no tener caja es un estado, no un 404.</param>
/// <param name="Companies">Viaja aquí porque el cajero no tiene companies.company.read y el selector de apertura la necesita.</param>
/// <param name="DefaultCompanyId">null cuando hay que elegir (cero o más de una activa).</param>
public sealed record RegisterContextResponse(
    PosCashierResponse Cashier,
    PosOpenSessionResponse? Session,
    IReadOnlyList<PosCompanyOption> Companies,
    Guid? DefaultCompanyId);

/// <param name="CashDifference">Con signo y calculada: negativo = faltante. La pantalla sólo elige el color.</param>
public sealed record PosSessionSummaryResponse(
    Guid Id,
    string Status,
    string CashierName,
    PosCompanyHeader Company,
    DateTimeOffset OpenedAtLocal,
    DateTimeOffset? ClosedAtLocal,
    decimal OpeningFloat,
    int SalesCount,
    int VoidedCount,
    decimal SalesTotal,
    IReadOnlyList<PosPaymentTotalResponse> PaymentTotals,
    decimal ExpectedCash,
    decimal? CountedCash,
    decimal? CashDifference,
    string? Note);

public sealed record PosPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <param name="TaxPercentage">Siempre presente: 0 sin tasa o con tasa inexistente (PosProductRef.TaxPercentage es int), igual que el tipo del frontend. Sólo la línea NotFound del preview lo lleva en null.</param>
/// <param name="UnsellableReason">Inactive, PriceMissing o NotFound (este último sólo en el preview). Un producto no vendible se muestra marcado en vez de esconderse: el cajero escanearía un código existente y vería "no existe".</param>
public sealed record PosProductResponse(
    Guid Id,
    string Code,
    string Name,
    decimal? UnitPrice,
    int TaxPercentage,
    string? ImageUrl,
    bool Sellable,
    string? UnsellableReason);

public sealed record PosPreviewLineRequest(Guid ProductId, decimal Quantity, decimal DiscountPercentage);

/// <param name="Code">null con NotFound: la pantalla lo toma del snapshot del carrito.</param>
public sealed record PosPreviewLineResponse(
    Guid ProductId,
    string? Code,
    string? Name,
    decimal Quantity,
    decimal? UnitPrice,
    int? TaxPercentage,
    decimal DiscountPercentage,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Subtotal,
    decimal LineTotal,
    bool Sellable,
    string? UnsellableReason);

/// <summary>
/// Existe para que el frontend nunca calcule IVA. Una línea no vendible vuelve marcada y fuera de
/// los totales, no como 422: el carrito tiene que poder mostrar qué está mal.
/// </summary>
/// <param name="ZeroTotalNotAllowed">Total 0 sin pos.sale.discount (también con todas las líneas no vendibles, que es lo que da el spec): la pantalla bloquea "Cobrar" con el motivo; sólo el POST responde 403.</param>
public sealed record PosPreviewResponse(
    IReadOnlyList<PosPreviewLineResponse> Lines,
    decimal Subtotal,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    bool ZeroTotalNotAllowed);

public sealed record PosSaleLineRequest(
    Guid ProductId,
    decimal Quantity,
    decimal DiscountPercentage,
    decimal ExpectedUnitPrice,
    int ExpectedTaxPercentage);

/// <param name="Method">Cash, Card o Transfer, por nombre exacto.</param>
/// <param name="Amount">Sólo Card/Transfer; en Cash lo calcula el servidor.</param>
/// <param name="Tendered">Sólo Cash: el billete del cliente.</param>
public sealed record PosPaymentRequest(string Method, decimal? Amount, decimal? Tendered, string? Reference);

public sealed record PosIssuerResponse(string Name, string TaxId, string? Address, string? Phone);

public sealed record PosCustomerResponse(string Name, string? IdentificationType, string IdentificationNumber);

public sealed record PosSaleLineResponse(
    int Position,
    Guid ProductId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercentage,
    int TaxPercentage,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal Subtotal,
    decimal LineTotal);

public sealed record PosTaxBreakdownResponse(int TaxPercentage, decimal Base, decimal TaxAmount);

public sealed record PosPaymentResponse(string Method, decimal Amount, decimal? Tendered, string? Reference);

public sealed record PosVoidResponse(string Reason, DateTimeOffset VoidedAtLocal, string VoidedByName);

/// <summary>
/// La respuesta de la venta y a la vez los datos del ticket: emisor congelado, hora local, desglose
/// de IVA y cambio. El frontend no arma nada.
/// </summary>
/// <param name="Voidable">Estado de la venta, no del que pregunta: Completed y su caja Open. Sin él la lista de ventas tendría que pedir una caja por fila.</param>
/// <param name="VoidBlockedReason">AlreadyVoided o SessionClosed; null con Voidable.</param>
public sealed record PosSaleResponse(
    Guid Id,
    string SaleNumber,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset CreatedAtLocal,
    Guid CashSessionId,
    string CashierName,
    PosIssuerResponse Issuer,
    PosCustomerResponse Customer,
    IReadOnlyList<PosSaleLineResponse> Lines,
    decimal Subtotal,
    decimal TaxAmount,
    decimal DiscountAmount,
    decimal Total,
    IReadOnlyList<PosTaxBreakdownResponse> TaxBreakdown,
    IReadOnlyList<PosPaymentResponse> Payments,
    decimal ChangeAmount,
    PosVoidResponse? Void,
    bool Voidable,
    string? VoidBlockedReason);

public sealed record PosSaleListItemResponse(
    Guid Id,
    string SaleNumber,
    DateTimeOffset CreatedAtLocal,
    string CashierName,
    string CustomerName,
    decimal Total,
    string Status,
    IReadOnlyList<string> PaymentMethods,
    bool Voidable,
    string? VoidBlockedReason);
