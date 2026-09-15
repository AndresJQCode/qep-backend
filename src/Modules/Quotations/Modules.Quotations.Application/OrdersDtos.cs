namespace Modules.Quotations.Application;

public sealed record OrderPaymentProofDto(Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt);

public sealed record OrderDto(
    Guid Id,
    string OrderNumber,
    Guid QuotationId,
    string Status,
    // PaymentStatus es texto y no el enum del dominio: ningún DTO expone un enum de dominio
    // directamente, mismo criterio que QuotationDto.Status.
    string PaymentStatus,
    string? Notes,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    /// <summary>Cuándo se aprobó y quién. Null mientras el pedido sigue pendiente de revisión.
    /// </summary>
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<OrderPaymentProofDto> PaymentProofs);

/// <summary>Un comprobante de pago, tal como viaja en el request de conversión (US-14): el
/// archivo ya se subió a Storage por fuera de este llamado, acá sólo se referencia.</summary>
public sealed record OrderPaymentProofRequest(Guid FileId, decimal Amount);

/// <summary>La corrección de un comprobante que ya existe (a pedido, 2026-09): a diferencia de
/// <see cref="OrderPaymentProofRequest"/>, lleva el id del comprobante a corregir en vez del
/// archivo — ese no cambia.</summary>
public sealed record OrderPaymentProofUpdateRequest(Guid ProofId, decimal Amount);

/// <summary>US-13 a US-16: el asistente de conversión. No lleva cliente/productos/totales —
/// todo eso se hereda de la cotización, que ya existe.</summary>
public sealed record ConvertQuotationToOrderRequest(
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs);

/// <summary>Sumar comprobantes a un pedido que ya existe, y de paso corregir el monto de los
/// que ya tenía cargados (a pedido, 2026-09) — un solo request para las dos cosas, en vez de uno
/// por cada comprobante que se toca.</summary>
public sealed record AddOrderPaymentProofsRequest(
    string PaymentStatus,
    IReadOnlyCollection<OrderPaymentProofRequest> PaymentProofs,
    /// <summary>Reemplaza <c>Order.Notes</c> entero (a pedido, 2026-09). La pantalla la precarga
    /// con lo que ya había, así que ausente o null la borra igual que al crear el pedido — no es
    /// un PATCH parcial.</summary>
    string? Notes = null,
    /// <summary>Comprobantes ya cargados cuyo monto se corrige (a pedido, 2026-09). El archivo
    /// no viaja: no cambia, sólo lo hace el importe. Ausente o vacío, no se corrige ninguno.
    /// Último parámetro a propósito: los tres anteriores ya existían y algún caller los pasa
    /// posicionalmente — agregar éste al final no les rompe el orden.</summary>
    IReadOnlyCollection<OrderPaymentProofUpdateRequest>? UpdatedProofs = null);

/// <summary>Una línea a agregar a la cotización de un pedido pendiente — mismo par que
/// <see cref="BatchQuotationItemAdditionRequest"/>, DTO propio porque viaja en un endpoint de
/// Orders y no de Quotations.</summary>
public sealed record OrderItemAdditionRequest(Guid ProductId, decimal Quantity);

/// <summary>
/// "Editar" un pedido pendiente para sumarle productos (a pedido, 2026-09): sólo mientras
/// <see cref="OrderStatus.Pending"/> — ver <see cref="Quotation.AddItemAfterConversion"/> y
/// <see cref="Order.RecalculatePaymentStatus"/>. Una tanda y no un producto por request, mismo
/// motivo que <see cref="BatchUpdateQuotationItemsRequest"/>: quien edita puede agregar varios
/// de una sola vez.
/// </summary>
public sealed record AddOrderItemsRequest(IReadOnlyList<OrderItemAdditionRequest> ToAdd);

public sealed record OrderPaymentProofResponse(Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt);

public sealed record OrderResponse(
    Guid Id,
    string OrderNumber,
    Guid QuotationId,
    string Status,
    string PaymentStatus,
    string? Notes,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<OrderPaymentProofResponse> PaymentProofs);

/// <summary>
/// Una fila del listado de pedidos (SALE-01). El pedido es 1:1 con su cotizacion y no repite
/// cliente, asesora, moneda ni totales -- los lee de ella--, asi que la fila los trae ya
/// resueltos: sin eso, quien pinta la tabla hace un GET por fila contra la cotizacion, y otro
/// contra Customers para poner un nombre.
/// </summary>
public sealed record OrderListItemResponse(
    Guid Id,
    string OrderNumber,
    /// <summary>La cotizacion de origen, para poder abrirla desde la fila.</summary>
    Guid QuotationId,
    string QuotationNumber,
    Guid ClientId,
    /// <summary>Null cuando el cliente ya no existe: <c>ClientId</c> es una referencia blanda
    /// entre modulos y un pedido historico tiene que poder leerse igual.</summary>
    string? ClientName,
    Guid AdvisorId,
    /// <summary>Correo de la asesora, no su nombre: el listado de pedidos sigue con el correo aunque
    /// el de cotizaciones ya muestre el nombre (spec 2026-09-11, D1, nota del 2026-09-14). Misma
    /// nulabilidad que <c>QuotationListItemResponse.AdvisorName</c>: referencia blanda entre
    /// módulos.</summary>
    string? AdvisorEmail,
    string Status,
    string PaymentStatus,
    /// <summary>La forma de pago de la cotizacion de origen -- la columna "Pago" del listado.
    /// Null en toda cotizacion creada desde que el editor dejo de pedirla, que es la razon por
    /// la que la columna hoy viene vacia y no un error de esta consulta.</summary>
    string? PaymentMethod,
    /// <summary>Cuando se convirtio. Es la fecha del pedido: el listado ordena y filtra por
    /// ella.</summary>
    DateTimeOffset ConvertedAt,
    /// <summary>La moneda de <c>Total</c>, heredada de la cuenta de cobro de la cotizacion.
    /// Viaja por fila con el mismo criterio que en cotizaciones.</summary>
    string Currency,
    decimal Total);

/// <summary>
/// El detalle de un pedido (SALE-04): el pedido y la cotizacion de la que salio, compuesta igual
/// que en su propio detalle --cliente resuelto, lineas con nombre y foto del producto, cuenta de
/// cobro--. Las dos en una respuesta y no dos llamadas encadenadas: el pedido no guarda cliente ni
/// productos, asi que la pantalla necesita las dos para dibujarse.
/// </summary>
public sealed record OrderDetailResponse(
    OrderResponse Order,
    QuotationResponse Quotation);

public sealed record OrdersPageResponse(
    IReadOnlyCollection<OrderListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);
