namespace Modules.Quotations.Application;

public sealed record SalePaymentProofDto(Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt);

public sealed record SaleDto(
    Guid Id,
    string SaleNumber,
    Guid QuotationId,
    string Status,
    // PaymentStatus es texto y no el enum del dominio: ningún DTO expone un enum de dominio
    // directamente, mismo criterio que QuotationDto.Status.
    string PaymentStatus,
    string? Notes,
    DateTimeOffset ConvertedAt,
    Guid ConvertedBy,
    /// <summary>Cuándo se aprobó y quién. Null mientras la venta sigue pendiente de revisión.
    /// </summary>
    DateTimeOffset? ApprovedAt,
    Guid? ApprovedBy,
    string? RitualCollectionSyncId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyCollection<SalePaymentProofDto> PaymentProofs);

/// <summary>Un comprobante de pago, tal como viaja en el request de conversión (US-14): el
/// archivo ya se subió a Storage por fuera de este llamado, acá sólo se referencia.</summary>
public sealed record SalePaymentProofRequest(Guid FileId, decimal Amount);

/// <summary>La corrección de un comprobante que ya existe (a pedido, 2026-09): a diferencia de
/// <see cref="SalePaymentProofRequest"/>, lleva el id del comprobante a corregir en vez del
/// archivo — ese no cambia.</summary>
public sealed record SalePaymentProofUpdateRequest(Guid ProofId, decimal Amount);

/// <summary>US-13 a US-16: el asistente de conversión. No lleva cliente/productos/totales —
/// todo eso se hereda de la cotización, que ya existe.</summary>
public sealed record ConvertQuotationToSaleRequest(
    string PaymentStatus,
    string? Notes,
    IReadOnlyCollection<SalePaymentProofRequest> PaymentProofs);

/// <summary>Sumar comprobantes a una venta que ya existe, y de paso corregir el monto de los
/// que ya tenía cargados (a pedido, 2026-09) — un solo request para las dos cosas, en vez de uno
/// por cada comprobante que se toca.</summary>
public sealed record AddSalePaymentProofsRequest(
    string PaymentStatus,
    IReadOnlyCollection<SalePaymentProofRequest> PaymentProofs,
    /// <summary>Reemplaza <c>Sale.Notes</c> entero (a pedido, 2026-09). La pantalla la precarga
    /// con lo que ya había, así que ausente o null la borra igual que al crear la venta — no es
    /// un PATCH parcial.</summary>
    string? Notes = null,
    /// <summary>Comprobantes ya cargados cuyo monto se corrige (a pedido, 2026-09). El archivo
    /// no viaja: no cambia, sólo lo hace el importe. Ausente o vacío, no se corrige ninguno.
    /// Último parámetro a propósito: los tres anteriores ya existían y algún caller los pasa
    /// posicionalmente — agregar éste al final no les rompe el orden.</summary>
    IReadOnlyCollection<SalePaymentProofUpdateRequest>? UpdatedProofs = null);

/// <summary>Una línea a agregar a la cotización de una venta pendiente — mismo par que
/// <see cref="BatchQuotationItemAdditionRequest"/>, DTO propio porque viaja en un endpoint de
/// Sales y no de Quotations.</summary>
public sealed record SaleItemAdditionRequest(Guid ProductId, decimal Quantity);

/// <summary>
/// "Editar" un pedido pendiente para sumarle productos (a pedido, 2026-09): sólo mientras
/// <see cref="SaleStatus.Pending"/> — ver <see cref="Quotation.AddItemAfterConversion"/> y
/// <see cref="Sale.RecalculatePaymentStatus"/>. Una tanda y no un producto por request, mismo
/// motivo que <see cref="BatchUpdateQuotationItemsRequest"/>: quien edita puede agregar varios
/// de una sola vez.
/// </summary>
public sealed record AddSaleItemsRequest(IReadOnlyList<SaleItemAdditionRequest> ToAdd);

public sealed record SalePaymentProofResponse(Guid Id, Guid FileId, decimal Amount, DateTimeOffset UploadedAt);

public sealed record SaleResponse(
    Guid Id,
    string SaleNumber,
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
    IReadOnlyCollection<SalePaymentProofResponse> PaymentProofs);

/// <summary>
/// Una fila del listado de ventas (SALE-01). La venta es 1:1 con su cotizacion y no repite
/// cliente, asesora, moneda ni totales -- los lee de ella--, asi que la fila los trae ya
/// resueltos: sin eso, quien pinta la tabla hace un GET por fila contra la cotizacion, y otro
/// contra Customers para poner un nombre.
/// </summary>
public sealed record SaleListItemResponse(
    Guid Id,
    string SaleNumber,
    /// <summary>La cotizacion de origen, para poder abrirla desde la fila.</summary>
    Guid QuotationId,
    string QuotationNumber,
    Guid ClientId,
    /// <summary>Null cuando el cliente ya no existe: <c>ClientId</c> es una referencia blanda
    /// entre modulos y una venta historica tiene que poder leerse igual.</summary>
    string? ClientName,
    Guid AdvisorId,
    /// <summary>Correo de la asesora, no su nombre: el listado de ventas sigue con el correo aunque
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
    /// <summary>Cuando se convirtio. Es la fecha de la venta: el listado ordena y filtra por
    /// ella.</summary>
    DateTimeOffset ConvertedAt,
    /// <summary>La moneda de <c>Total</c>, heredada de la cuenta de cobro de la cotizacion.
    /// Viaja por fila con el mismo criterio que en cotizaciones.</summary>
    string Currency,
    decimal Total);

/// <summary>
/// El detalle de una venta (SALE-04): la venta y la cotizacion de la que salio, compuesta igual
/// que en su propio detalle --cliente resuelto, lineas con nombre y foto del producto, cuenta de
/// cobro--. Las dos en una respuesta y no dos llamadas encadenadas: la venta no guarda cliente ni
/// productos, asi que la pantalla necesita las dos para dibujarse.
/// </summary>
public sealed record SaleDetailResponse(
    SaleResponse Sale,
    QuotationResponse Quotation);

public sealed record SalesPageResponse(
    IReadOnlyCollection<SaleListItemResponse> Items,
    int Total,
    int Page,
    int PageSize);
