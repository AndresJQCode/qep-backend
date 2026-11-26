namespace Modules.Store.Application;

/// <summary>La división político-administrativa de Colombia (DIVIPOLA), la única que la tienda ofrece con ids.</summary>
public interface IStoreGeography
{
    Task<IReadOnlyList<StoreGeoItem>> DepartmentsAsync(CancellationToken cancellationToken);

    /// <summary>Null cuando el departamento no existe; una lista vacía sería un departamento sin ciudades.</summary>
    Task<IReadOnlyList<StoreGeoItem>?> CitiesAsync(Guid departmentId, CancellationToken cancellationToken);

    Task<StoreCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken);
}

public sealed record StoreGeoItem(Guid Id, string Name);

public sealed record StoreCityRef(Guid CityId, string CityName, Guid DepartmentId, string DepartmentName);
