namespace Modules.Support.Application;

/// <summary>Un departamento o una ciudad de Geography, con lo que dibuja el formulario.</summary>
public sealed record SupportGeoItem(Guid Id, string Name);

/// <summary>
/// La división de Colombia del formulario público (plan F19), con adaptador en Bootstrapper sobre Geography (precedente
/// <c>IStoreGeography</c>). Geography no tiene tenant: nada se acota.
/// </summary>
public interface ISupportGeography
{
    Task<IReadOnlyList<SupportGeoItem>> DepartmentsAsync(CancellationToken cancellationToken);

    /// <summary>Null cuando el departamento no existe; una lista vacía sería un departamento sin ciudades.</summary>
    Task<IReadOnlyList<SupportGeoItem>?> CitiesAsync(Guid departmentId, CancellationToken cancellationToken);

    /// <summary>La ciudad con su nombre, o null si no existe.</summary>
    Task<SupportGeoItem?> FindCityAsync(Guid cityId, CancellationToken cancellationToken);
}
