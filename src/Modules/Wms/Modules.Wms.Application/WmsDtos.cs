namespace Modules.Wms.Application;

/// <summary>
/// Una bodega para la lista y el formulario. <c>LocationCount</c> excluye las virtuales: la lista de bodegas muestra «ubicaciones
/// configuradas» y las cuatro virtuales (SUPPLIER, CUSTOMER, ADJUSTMENT, TRANSIT) existen siempre, así que contarlas haría que una
/// bodega recién creada dijera tener ubicaciones que nadie configuró.
/// </summary>
public sealed record WarehouseDto(
    Guid Id,
    string Code,
    string Name,
    Guid? CityId,
    string? CityName,
    bool IsActive,
    long Version,
    int LocationCount);

public sealed record WarehousesDto(IReadOnlyList<WarehouseDto> Items);

/// <summary>Cuerpo de <c>POST /warehouses</c>. El código lo normaliza el dominio (P-4): «bog» queda «BOG».</summary>
public sealed record CreateWarehouseRequest(string Code, string Name, Guid? CityId);

/// <summary>Cuerpo de <c>PUT /warehouses/{id}</c>. No lleva código: el código es la clave de las etiquetas y no se cambia.</summary>
public sealed record UpdateWarehouseRequest(string Name, Guid? CityId, bool IsActive, long Version);

/// <summary>Cuerpo de <c>POST /warehouses/{id}/locations</c>. <c>Kind</c> y <c>Role</c> viajan por nombre; el código lo normaliza el
/// dominio (P-4).</summary>
public sealed record LocationRequest(
    Guid? ParentId,
    string Code,
    string Kind,
    string Role,
    bool AllowsStorage,
    bool AllowsPicking,
    decimal? MaxWeightKg,
    decimal? MaxVolumeM3,
    int? MaxUnits,
    int PutawayPriority,
    IReadOnlyList<string>? StorageClasses);

/// <summary>Cuerpo de <c>PUT …/locations/{id}</c>: lo mismo más el <c>status</c> y la versión. No hay <c>isActive</c>: una ubicación
/// se bloquea o se borra.</summary>
public sealed record UpdateLocationRequest(
    Guid? ParentId,
    string Code,
    string Kind,
    string Role,
    bool AllowsStorage,
    bool AllowsPicking,
    decimal? MaxWeightKg,
    decimal? MaxVolumeM3,
    int? MaxUnits,
    int PutawayPriority,
    IReadOnlyList<string>? StorageClasses,
    string Status,
    long Version);

/// <summary>Un nodo del árbol, plano: el cliente arma el árbol con <c>ParentId</c> y se ordena por <c>Path</c>.</summary>
public sealed record LocationDto(
    Guid Id,
    Guid WarehouseId,
    Guid? ParentId,
    string Code,
    string Path,
    string Kind,
    string Role,
    bool IsVirtual,
    bool AllowsStorage,
    bool AllowsPicking,
    decimal? MaxWeightKg,
    decimal? MaxVolumeM3,
    int? MaxUnits,
    int PutawayPriority,
    IReadOnlyList<string> StorageClasses,
    string Status,
    long Version);

public sealed record LocationsDto(IReadOnlyList<LocationDto> Items);

/// <summary>Lo mínimo que la app de bodega necesita para resolver un código escaneado, sin el peso del árbol completo.</summary>
public sealed record ScannableLocationDto(Guid Id, string Code, string Path, string Role);

/// <summary>El <c>Etag</c> deja que la app guarde la lista y sólo la baje otra vez cuando cambió una ubicación no virtual (las
/// virtuales no se escanean ni cambian).</summary>
public sealed record ScannableLocationsDto(IReadOnlyList<ScannableLocationDto> Items, string Etag);

/// <summary>Cuerpo de <c>POST /owners</c>. Siempre crea un <c>Client</c>: el <c>Self</c> sólo lo escribe el provisioner.</summary>
public sealed record CreateOwnerRequest(string Name, string? Identification, string? ContactEmail);

/// <summary>Cuerpo de <c>PUT /owners/{id}</c>.</summary>
public sealed record UpdateOwnerRequest(string Name, string? Identification, string? ContactEmail, bool IsActive, long Version);

/// <summary><c>Kind</c> viaja por nombre (<c>Self</c> | <c>Client</c>): la pantalla oculta el listado cuando sólo está el Self.</summary>
public sealed record OwnerDto(Guid Id, string Kind, string Name, string? Identification, string? ContactEmail, bool IsActive, long Version);

public sealed record OwnersDto(IReadOnlyList<OwnerDto> Items);
