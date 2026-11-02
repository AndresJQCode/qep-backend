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
