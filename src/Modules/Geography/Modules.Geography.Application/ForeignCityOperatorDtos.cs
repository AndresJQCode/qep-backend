namespace Modules.Geography.Application;

// Spec 2026-11-26 «Consola del operador»: la forma de cada DTO es contrato con qep-frontend. JSON en camelCase; los estados
// viajan con el nombre del enum (Pending, Discarded). Todo lo que la pantalla necesita para dibujar una ciudad del exterior
// viaja junto: la lista de Siigo repite nombre y departamento, así que el código de la ciudad es lo único que las distingue.

/// <summary>El país de una fila o un sinónimo, con el nombre que la pantalla pinta junto al código: la pantalla no tiene la
/// lista de países cargada.</summary>
public sealed record BillingCountryRefDto(string Code, string Name);

/// <summary>Un país del catálogo de Siigo con las filas <c>Pending</c> que tiene. Viaja el conteo aunque sea cero: el selector
/// de país pone primero a los que tienen pendientes y no debería pedirlos aparte.</summary>
public sealed record BillingCountryDto(string Code, string Name, int PendingCount);

/// <summary>Una ciudad de la lista de Siigo. Lleva el departamento y el código porque la lista repite nombres dentro de un país
/// (<c>Boston</c>, <c>Massachusetts</c>, <c>2101</c> y <c>02119</c>): sin ellos el operador no distingue una de otra.</summary>
public sealed record BillingCityRefDto(Guid Id, string Name, string StateName, string CityCode);

/// <summary>Una ciudad parecida al texto, con el mismo desglose que <see cref="BillingCityRefDto"/>;
/// <see cref="Similarity"/> es un entero de 0 a 100 que la pantalla pinta tal cual (mínimo 50).</summary>
public sealed record BillingCitySuggestionDto(Guid Id, string Name, string StateName, string CityCode, int Similarity);

/// <summary>
/// Una fila de la bandeja del exterior. Trae los conteos y la mejor sugerencia ya calculados porque la tabla los pinta por
/// fila: pedirlos aparte serían consultas por fila, y la sugerencia necesita las ciudades del país, que la pantalla no tiene.
/// Es también lo que devuelven descartar y restaurar, para que la tabla repinte la fila sin volver a pedir la página.
/// </summary>
public sealed record UnresolvedForeignCityListItemDto(
    Guid Id,
    string Text,
    string NormalizedText,
    BillingCountryRefDto Country,
    string Status,
    int OrderCount,
    int TenantCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    BillingCitySuggestionDto? BestSuggestion,
    long Version);

/// <summary>Una página de la bandeja del exterior; <see cref="Page"/> es la pedida, nunca un recorte del servidor: la pantalla
/// distingue «no hay nada» de «te pasaste de página» comparándola con <see cref="Total"/>.</summary>
public sealed record UnresolvedForeignCityPageDto(
    IReadOnlyList<UnresolvedForeignCityListItemDto> Items, int Total, int Page, int PageSize);

/// <summary>
/// El panel de detalle de un texto del exterior: los tenants donde apareció y hasta tres sugerencias, todo en una respuesta
/// porque el panel se abre desde la fila y pinta todo junto. Una fila <c>Resolved</c> o <c>Discarded</c> responde con su
/// <see cref="Status"/>, no 404: la pantalla abierta cuando otro la resolvió lo muestra.
/// </summary>
public sealed record UnresolvedForeignCityDetailDto(
    Guid Id,
    string Text,
    string NormalizedText,
    BillingCountryRefDto Country,
    string Status,
    int OrderCount,
    int TenantCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    BillingCitySuggestionDto? BestSuggestion,
    long Version,
    IReadOnlyList<UnresolvedCityTenantDto> Tenants,
    IReadOnlyList<BillingCitySuggestionDto> Suggestions);

/// <summary>Una fila de la lista de sinónimos del exterior, y lo que devuelven confirmar y corregir. Sin contadores de
/// corrección: el exterior no corrige pedidos ya hechos, sólo decide qué ciudad usa la próxima factura.</summary>
public sealed record ForeignCitySynonymDto(
    Guid Id,
    string Text,
    string NormalizedText,
    BillingCountryRefDto Country,
    BillingCityRefDto City,
    CitySynonymActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>Una página de sinónimos del exterior; <see cref="Page"/> es la pedida, como en
/// <see cref="UnresolvedForeignCityPageDto"/>.</summary>
public sealed record ForeignCitySynonymPageDto(IReadOnlyList<ForeignCitySynonymDto> Items, int Total, int Page, int PageSize);
