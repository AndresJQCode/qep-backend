using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>
/// Spec 2026-10-26 §6: los límites de la paginación y la búsqueda de la consola de ciudades, los mismos de la consola de tenants
/// (<c>OperatorInput</c> de Tenancy, que es interna). El estado llega como texto y se valida <b>antes</b> de convertir, sin ignorar
/// mayúsculas: convertir primero haría de un texto desconocido un 500.
/// </summary>
public static class CityOperatorInput
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    /// <summary>Tope de <c>page</c>: con él, <c>(page - 1) * pageSize</c> no da la vuelta a un OFFSET negativo con ningún
    /// <c>pageSize</c> válido.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    /// <summary>La bandeja sólo lista <c>Pending</c> y <c>Discarded</c>: una fila <c>Resolved</c> ya es un sinónimo, y se ve en
    /// la lista de sinónimos.</summary>
    public static bool IsListStatus(string value) =>
        string.Equals(value, nameof(UnresolvedCityStatus.Pending), StringComparison.Ordinal)
        || string.Equals(value, nameof(UnresolvedCityStatus.Discarded), StringComparison.Ordinal);

    /// <summary>Ausente es <c>Pending</c>. Sólo se llama después de validar.</summary>
    public static UnresolvedCityStatus ParseListStatus(string? value) =>
        value is null ? UnresolvedCityStatus.Pending : Enum.Parse<UnresolvedCityStatus>(value, ignoreCase: false);
}
