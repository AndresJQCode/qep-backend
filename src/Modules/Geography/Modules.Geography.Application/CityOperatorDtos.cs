namespace Modules.Geography.Application;

// Spec 2026-10-26 §6 y «Contrato HTTP para el frontend» del plan: la forma de cada DTO es contrato con qep-frontend, que ya está
// construido contra ella. JSON en camelCase; los estados viajan con el nombre del enum (Pending, Approved), porque el diccionario
// lo tiene el frontend; todo grupo de impacto viaja aunque esté en cero.

/// <summary>El departamento de una fila o un sinónimo, con el nombre y el código que la consola de ciudades pinta junto al texto:
/// el texto solo no dice de qué departamento es, y la pantalla no tiene la lista de departamentos cargada.</summary>
public sealed record DepartmentRefDto(Guid Id, string Name, string DivipolaCode);

/// <summary>Una ciudad elegida o sugerida, con nombre y código, para que el modal de confirmar la muestre sin otra consulta.</summary>
public sealed record CityRefDto(Guid Id, string Name, string DivipolaCode);

/// <summary>Una ciudad parecida al texto. Lleva los dos nombres porque la bandeja muestra el común («CALI») y aclara el oficial
/// («SANTIAGO DE CALI»); <see cref="Similarity"/> es un entero de 0 a 100 que la pantalla pinta tal cual (mínimo 50).</summary>
public sealed record CitySuggestionDto(Guid CityId, string Name, string DivipolaCode, string DivipolaName, int Similarity);

/// <summary>Las cuatro tarjetas de arriba de la consola de ciudades: siempre las cuatro, también en cero.
/// <see cref="OrdersWaitingCount"/> son las ocurrencias de filas <c>Pending</c>, sin mirar el estado del pedido.</summary>
public sealed record CitySummaryDto(int PendingCount, int DiscardedCount, int SynonymCount, int OrdersWaitingCount);

/// <summary>
/// Una fila de la bandeja de ciudades sin resolver. Trae los conteos y la mejor sugerencia ya calculados porque la tabla los pinta
/// por fila: pedirlos aparte serían una consulta por fila, y la sugerencia necesita las ciudades del departamento, que la pantalla
/// no tiene. Es también lo que devuelven descartar y restaurar, para que la tabla repinte la fila sin volver a pedir la página.
/// </summary>
public sealed record UnresolvedCityListItemDto(
    Guid Id,
    string Text,
    string NormalizedText,
    DepartmentRefDto Department,
    string Status,
    int OrderCount,
    int TenantCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    CitySuggestionDto? BestSuggestion,
    long Version);

/// <summary>Una página de la bandeja. <see cref="Page"/> es la pedida, nunca un recorte del servidor: la pantalla distingue «no hay
/// nada» de «te pasaste de página» comparándola con <see cref="Total"/>.</summary>
public sealed record UnresolvedCityPageDto(IReadOnlyList<UnresolvedCityListItemDto> Items, int Total, int Page, int PageSize);

/// <summary>Un tenant donde apareció el texto, con su nombre: el detalle lista a quién afecta, y Geography no ve los tenants.</summary>
public sealed record UnresolvedCityTenantDto(Guid TenantId, string Name, int OrderCount);

/// <summary>
/// El panel de detalle de un texto sin resolver: la muestra de dirección, los tenants y hasta tres sugerencias, todo en una
/// respuesta porque el panel se abre desde la fila y pinta todo junto. Una fila <c>Resolved</c> o <c>Discarded</c> responde con su
/// <see cref="Status"/> (y <see cref="SynonymId"/> si se resolvió), no 404: la pantalla abierta cuando otro la resolvió lo muestra.
/// </summary>
public sealed record UnresolvedCityDetailDto(
    Guid Id,
    string Text,
    string NormalizedText,
    DepartmentRefDto Department,
    string Status,
    string SampleAddress,
    string? ProvinceCode,
    int OrderCount,
    IReadOnlyList<UnresolvedCityTenantDto> Tenants,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<CitySuggestionDto> Suggestions,
    Guid? SynonymId,
    long Version);

/// <summary>Cuántos de un grupo de impacto son de un tenant, con su nombre: el modal desglosa cada grupo por tenant.</summary>
public sealed record ImpactTenantCountDto(Guid TenantId, string Name, int Count);

/// <summary>Un grupo del impacto. Viaja aunque esté en cero (<c>count: 0, byTenant: []</c>): la pantalla dibuja cada grupo siempre,
/// y uno que faltara la obligaría a saber cuáles existen.</summary>
public sealed record ImpactCountDto(int Count, IReadOnlyList<ImpactTenantCountDto> ByTenant);

/// <summary>Un pedido que no se toca porque ya no está <c>Pending</c>. <see cref="Status"/> es el nombre del enum de Quotations
/// (<c>Approved</c>, <c>Invoiced</c>, <c>Cancelled</c>).</summary>
public sealed record ImpactOrderDto(Guid TenantId, string TenantName, Guid OrderId, string OrderNumber, string Status);

/// <summary>Los pedidos que no se tocan, completos y sin tope: el modal los lista uno por uno para que el operador avise a cada
/// tenant, y una lista recortada escondería justo a quién avisar.</summary>
public sealed record ImpactOrderListDto(int Count, IReadOnlyList<ImpactOrderDto> Orders);

/// <summary>El modal de confirmar un texto como sinónimo: qué pasa con cada pedido y cliente antes de escribir nada (§4.4). Todos
/// los grupos viajan, también en cero.</summary>
public sealed record ResolveImpactDto(
    CityRefDto City,
    ImpactCountDto OrdersToFix,
    ImpactCountDto OrdersMissingData,
    ImpactCountDto OrdersWithShipping,
    ImpactOrderListDto OrdersNotPending,
    ImpactCountDto CustomersToComplete,
    ImpactCountDto CustomersStayIncomplete);

/// <summary>El modal de corregir un sinónimo a otra ciudad (§4.6): de dónde a dónde y qué se recorrige. Todos los grupos viajan,
/// también en cero.</summary>
public sealed record CorrectImpactDto(
    CityRefDto From,
    CityRefDto To,
    ImpactCountDto OrdersToChange,
    ImpactCountDto OrdersEdited,
    ImpactOrderListDto OrdersNotPending,
    ImpactCountDto CustomersToChange,
    ImpactCountDto CustomersEdited,
    ImpactCountDto OccurrencesUncorrected);

/// <summary>Quién creó un sinónimo. <see cref="Email"/> es nulo si el usuario ya no existe: la lista muestra el correo, y el id
/// queda para cuando no hay.</summary>
public sealed record CitySynonymActorDto(Guid SubjectId, string? Email);

/// <summary>
/// Una fila de la lista de sinónimos, y lo que devuelven confirmar, corregir y reintentar. Trae los cuatro contadores de
/// corrección porque la tabla los pinta por fila y la pantalla de confirmar sondea el progreso con este mismo DTO.
/// </summary>
public sealed record CitySynonymDto(
    Guid Id,
    string Text,
    string NormalizedText,
    DepartmentRefDto Department,
    CityRefDto City,
    int CorrectedOrders,
    int CorrectedCustomers,
    int PendingCorrections,
    int FailedCorrections,
    CitySynonymActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>Una página de sinónimos; <see cref="Page"/> es la pedida, como en <see cref="UnresolvedCityPageDto"/>.</summary>
public sealed record CitySynonymPageDto(IReadOnlyList<CitySynonymDto> Items, int Total, int Page, int PageSize);

/// <summary>El resultado del recorrido histórico, que el botón muestra como resumen: los cinco contadores siempre, en cero
/// también.</summary>
public sealed record CityBackfillResultDto(int Scanned, int Reported, int AlreadyReported, int NowMatching, int Skipped);
