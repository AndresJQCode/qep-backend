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
/// <param name="DefaultCurrency">La vigente del tenant, para la pantalla sin caja; con caja abierta manda Session.Currency.</param>
public sealed record RegisterContextResponse(
    PosCashierResponse Cashier,
    PosOpenSessionResponse? Session,
    IReadOnlyList<PosCompanyOption> Companies,
    Guid? DefaultCompanyId,
    string DefaultCurrency);

/// <param name="Currency">La de la caja, congelada al abrir: la lista y el cierre no la toman de la caja abierta hoy.</param>
/// <param name="CashDifference">Con signo y calculada: negativo = faltante. La pantalla sólo elige el color.</param>
public sealed record PosSessionSummaryResponse(
    Guid Id,
    string Status,
    string CashierName,
    PosCompanyHeader Company,
    DateTimeOffset OpenedAtLocal,
    DateTimeOffset? ClosedAtLocal,
    decimal OpeningFloat,
    string Currency,
    int SalesCount,
    int VoidedCount,
    decimal SalesTotal,
    IReadOnlyList<PosPaymentTotalResponse> PaymentTotals,
    decimal ExpectedCash,
    decimal? CountedCash,
    decimal? CashDifference,
    string? Note);

public sealed record PosPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>
/// Una variante vendible en la caja (spec 2026-10-20 §5): la respuesta del escáner y cada opción
/// del selector de un producto de la cuadrícula. Es lo que el carrito guarda y lo que la venta
/// manda (<see cref="PosSaleLineRequest.VariantId"/>).
/// </summary>
/// <param name="VariantId">El id que va en la venta y en el preview: el carrito junta por variante, no por producto.</param>
/// <param name="ProductId">El producto dueño: la pantalla lo usa para saber qué tarjeta de la cuadrícula es.</param>
/// <param name="Code">El <c>Code</c> de la variante.</param>
/// <param name="Barcode">El código de barras de la variante, si tiene.</param>
/// <param name="Name">El nombre visible ya armado («Camiseta – M / Azul»; el del producto si no tiene opciones). El carrito y el ticket lo pintan tal cual: el frontend no concatena.</param>
/// <param name="TaxPercentage">Siempre presente: 0 sin tasa o con tasa inexistente (PosProductRef.TaxPercentage es int), igual que el tipo del frontend. Sólo la línea NotFound del preview lo lleva en null.</param>
/// <param name="UnsellableReason">Inactive, PriceMissing o NotFound (este último sólo en el preview). Un producto no vendible se muestra marcado en vez de esconderse: el cajero escanearía un código existente y vería "no existe".</param>
public sealed record PosProductResponse(
    Guid VariantId,
    Guid ProductId,
    string Code,
    string? Barcode,
    string Name,
    decimal? UnitPrice,
    int TaxPercentage,
    string? ImageUrl,
    bool Sellable,
    string? UnsellableReason);

/// <summary>
/// Un producto de la cuadrícula del POS con sus variantes vendibles (spec 2026-10-20 §5). BFF: la
/// cuadrícula muestra productos y, al tocar uno con opciones, el selector elige entre
/// <see cref="Variants"/> sin otra llamada; uno sin opciones manda su única variante directo al
/// carrito. Por eso cada variante viaja ya resuelta (precio en la moneda de la caja, nombre visible
/// y si se puede vender), igual que la respuesta del escáner.
/// </summary>
/// <param name="Name">El nombre del producto, sin opciones: es el título de la tarjeta.</param>
/// <param name="HasOptions">true: tocar la tarjeta abre el selector. false: <see cref="Variants"/> trae una sola y va directo al carrito. Viaja aparte para que la pantalla no lo deduzca del número de variantes.</param>
/// <param name="ImageUrl">La portada del producto (la foto principal).</param>
/// <param name="Variants">Las variantes activas, en el orden del producto.</param>
public sealed record PosCatalogProductResponse(
    Guid ProductId,
    string Name,
    bool HasOptions,
    string? ImageUrl,
    IReadOnlyList<PosProductResponse> Variants);

/// <summary>
/// Un chip de categoría de la caja (spec 2026-10-26 §3.4). La lista viaja plana y en orden por nombre;
/// la pantalla arma el árbol con <see cref="ParentId"/>.
/// </summary>
/// <param name="ParentId">null en una raíz.</param>
/// <param name="ProductCount">Productos activos de la categoría **y de todas sus descendientes**: lo que
/// encuentra <c>GET /pos/products?categoryId=</c>. Viaja ya acumulado porque el navegador no puede sumarlo
/// sin conocer todo el árbol, y una categoría en 0 viaja igual.</param>
/// <param name="QuickAccessPosition">Lugar entre los chips de acceso rápido que eligió el tenant (1..N,
/// contiguos); null si no está fijada. La lista sigue por nombre: la pantalla ordena las fijadas por
/// este campo (spec 2026-10-26-categorias-pos-acceso-rapido).</param>
public sealed record PosCategoryResponse(Guid Id, string Name, Guid? ParentId, int ProductCount, int? QuickAccessPosition);

/// <summary>Las categorías de la caja.</summary>
/// <param name="Total">BFF: es lo que pinta «Todas las categorías · N productos», incluidos los productos
/// activos sin categoría. Sumar las raíces en el cliente los perdería.</param>
public sealed record PosCategoriesResponse(IReadOnlyList<PosCategoryResponse> Items, int Total);

public sealed record PosPreviewLineRequest(Guid VariantId, decimal Quantity, decimal DiscountPercentage);

/// <param name="ProductId">null con NotFound, como Code y Name.</param>
/// <param name="Code">null con NotFound: la pantalla lo toma del snapshot del carrito.</param>
/// <param name="Name">El nombre visible de la variante.</param>
public sealed record PosPreviewLineResponse(
    Guid VariantId,
    Guid? ProductId,
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

/// <param name="VariantId">La variante que se vende (spec 2026-10-20 §4): para un producto sin opciones, el id de su variante por defecto.</param>
public sealed record PosSaleLineRequest(
    Guid VariantId,
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

/// <summary>
/// El selector de la caja (spec «Endpoints nuevos»): sin teléfono, correo, dirección ni clasificación, porque el
/// cajero no tiene el padrón (decisión 4). IsActive viaja para el 422 de identification_taken: la caja dice por qué
/// no puede elegir a un inactivo; en la búsqueda siempre es true.
/// </summary>
public sealed record PosCustomerOptionResponse(
    Guid Id, string Name, string IdentificationType, string IdentificationNumber, bool IsComplete, bool IsActive);

/// <summary>Un selector, no una lista paginada: siempre hasta 20, sin total.</summary>
public sealed record PosCustomerOptionsResponse(IReadOnlyList<PosCustomerOptionResponse> Items);

/// <param name="Name">La foto del nombre visible de la variante al vender: es lo que imprime el ticket.</param>
public sealed record PosSaleLineResponse(
    int Position,
    Guid VariantId,
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
/// <param name="Currency">La de la venta, copiada de su caja: todos los importes están en ella.</param>
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
    string Currency,
    IReadOnlyList<PosTaxBreakdownResponse> TaxBreakdown,
    IReadOnlyList<PosPaymentResponse> Payments,
    decimal ChangeAmount,
    PosVoidResponse? Void,
    bool Voidable,
    string? VoidBlockedReason);

/// <param name="Currency">La de la venta: una fila de una caja en otra moneda no se rotula con la de hoy.</param>
public sealed record PosSaleListItemResponse(
    Guid Id,
    string SaleNumber,
    DateTimeOffset CreatedAtLocal,
    string CashierName,
    string CustomerName,
    decimal Total,
    string Currency,
    string Status,
    IReadOnlyList<string> PaymentMethods,
    bool Voidable,
    string? VoidBlockedReason);
