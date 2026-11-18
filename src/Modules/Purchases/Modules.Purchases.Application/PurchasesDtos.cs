using System.Text.Json.Serialization;

namespace Modules.Purchases.Application;

/// <summary>El proveedor de una fila de la lista: sólo lo que la grilla pinta, sin estado (la lista no ofrece acciones).</summary>
public sealed record PurchaseOrderSupplierRefDto(Guid Id, string Name);

/// <summary>
/// El proveedor del detalle. Trae <c>IsActive</c> porque la pantalla explica por qué «Enviar» respondería 422
/// <c>supplier_inactive</c> sin pedir el proveedor aparte (D-4).
/// </summary>
public sealed record PurchaseOrderSupplierDto(Guid Id, string Name, bool IsActive);

/// <summary>Quién creó la orden con su nombre visible ya resuelto: la pantalla no cruza ids con el padrón.</summary>
public sealed record PurchaseOrderMemberDto(Guid MemberId, string Name);

/// <summary>
/// Una línea del detalle. Código, nombre y tasa son la foto que guardó la orden, no el catálogo de hoy. <c>LineStatus</c> y
/// <c>Subtotal</c> (sin IVA, con lo confirmado si existe) los calcula el backend: la pantalla nunca multiplica.
/// </summary>
public sealed record PurchaseOrderLineDto(
    Guid Id,
    Guid? VariantId,
    string Code,
    string Name,
    decimal? Quantity,
    decimal? ConfirmedQuantity,
    decimal UnitCost,
    decimal? ConfirmedUnitCost,
    decimal TaxRatePercent,
    string LineStatus,
    decimal Subtotal);

/// <summary>Subtotal sin IVA, IVA y total, ya redondeados por línea (<c>PurchaseOrderMath</c>): la pantalla no suma.</summary>
public sealed record PurchaseOrderTotalsDto(decimal Subtotal, decimal Tax, decimal Total);

/// <summary>Un paso del ciclo con el nombre del miembro ya resuelto (<c>"Sin nombre"</c> si la membresía no está).</summary>
public sealed record PurchaseOrderHistoryDto(string Status, DateTimeOffset At, string ActorName);

/// <summary>
/// La orden completa para la pantalla de detalle (BFF): todo lo que pinta llega calculado. Los costos de las líneas van
/// <b>sin IVA</b> y el IVA se suma en los totales. <c>Totals</c> usa lo confirmado en las líneas que lo tienen y
/// <c>RequestedTotal</c> lo pedido. <c>AllowedActions</c> ya cruza estado y permisos (en minúscula, en el orden del contrato:
/// <c>"edit"</c>, <c>"delete"</c>, <c>"send"</c>, <c>"confirm"</c>, <c>"receive"</c>, <c>"cancel"</c>); sin
/// <c>purchases.order.manage</c> llega vacío.
/// </summary>
public sealed record PurchaseOrderDetailDto(
    Guid Id,
    string Number,
    string Status,
    bool ConfirmedWithChanges,
    long Version,
    PurchaseOrderSupplierDto Supplier,
    string Currency,
    string? Notes,
    PurchaseOrderMemberDto CreatedBy,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    PurchaseOrderTotalsDto Totals,
    decimal RequestedTotal,
    IReadOnlyList<PurchaseOrderHistoryDto> History,
    IReadOnlyList<string> AllowedActions);

/// <summary>
/// Una fila de la lista. <c>Total</c> sigue la regla de <c>totals.total</c> del detalle y <c>LineCount</c> viene contado, para
/// que la grilla no pida el detalle de cada orden.
/// </summary>
public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string Number,
    string Status,
    bool ConfirmedWithChanges,
    PurchaseOrderSupplierRefDto Supplier,
    string Currency,
    decimal Total,
    DateTimeOffset CreatedAt,
    int LineCount);

/// <summary>
/// Cuántas órdenes hay en cada estado con los filtros de la lista salvo <c>status</c>, para las pestañas (D-18). Las claves del
/// contrato son el nombre del estado tal cual (<c>Draft</c>…<c>Cancelled</c>) y <c>all</c> en minúscula, así que cada
/// propiedad lleva su <c>JsonPropertyName</c> explícito y la política camelCase de la API no las toca. Siempre vienen las seis,
/// también en cero; <c>All</c> es la suma de las cinco.
/// </summary>
public sealed record PurchaseOrderStatusCountsDto(
    [property: JsonPropertyName("Draft")] int Draft,
    [property: JsonPropertyName("Sent")] int Sent,
    [property: JsonPropertyName("Confirmed")] int Confirmed,
    [property: JsonPropertyName("Received")] int Received,
    [property: JsonPropertyName("Cancelled")] int Cancelled,
    [property: JsonPropertyName("all")] int All);

/// <summary>El sobre de paginación de <c>GET /suppliers</c> más <c>Counts</c>, que la pantalla nunca suma.</summary>
public sealed record PurchaseOrdersResponse(
    IReadOnlyCollection<PurchaseOrderSummaryDto> Items,
    int Total,
    int Page,
    int PageSize,
    PurchaseOrderStatusCountsDto Counts);

/// <summary>
/// Una línea de la vista previa: lo que el alta guardaría (código, nombre y tasa actuales del catálogo) y su subtotal sin IVA.
/// </summary>
public sealed record PurchaseOrderPreviewLineDto(
    Guid VariantId,
    string Code,
    string Name,
    decimal TaxRatePercent,
    decimal Subtotal);

/// <summary>
/// La vista previa del formulario (con debounce): misma aritmética que el alta (D-19), así el total que se ve es el que se
/// guarda. Las líneas vuelven en el orden del cuerpo.
/// </summary>
public sealed record PurchaseOrderPreviewDto(IReadOnlyList<PurchaseOrderPreviewLineDto> Lines, PurchaseOrderTotalsDto Totals);

/// <summary>Una línea del cuerpo: la variante, la cantidad y el costo unitario sin IVA.</summary>
public sealed record PurchaseOrderLineInput(Guid VariantId, decimal Quantity, decimal UnitCost);

/// <summary>
/// El cuerpo de <c>POST /</c>, <c>PUT /{id}</c> y <c>POST /preview</c>: un solo record porque el contrato dice «igual que
/// POST». Código, nombre y tasa no viajan: los pone el backend desde el catálogo.
/// </summary>
public sealed record PurchaseOrderWriteRequest(
    Guid SupplierId,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineInput> Lines);

/// <summary>Lo que el proveedor confirmó para una línea. Cantidad 0 la rechaza; el costo va igual (D-9).</summary>
public sealed record PurchaseOrderLineConfirmationInput(Guid LineId, decimal ConfirmedQuantity, decimal ConfirmedUnitCost);

/// <summary>El cuerpo de <c>POST /{id}/confirm</c>: todas las líneas de la orden, cada una una vez.</summary>
public sealed record ConfirmPurchaseOrderRequest(IReadOnlyList<PurchaseOrderLineConfirmationInput> Lines);
