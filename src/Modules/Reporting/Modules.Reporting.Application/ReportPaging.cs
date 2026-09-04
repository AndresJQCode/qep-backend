namespace Modules.Reporting.Application;

/// <summary>
/// Mismos defaults que <c>ProductPaging</c> y <c>CustomerPaging</c>. Duplicado por módulo a
/// propósito, como el resto: el tamaño de página es una decisión de cada listado, y compartir la
/// clase ataría cuatro reportes a un cambio pensado para otro módulo.
/// </summary>
public static class ReportPaging
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Tope duro. El tamaño de página lo elige el cliente, así que sin límite un
    /// <c>?pageSize=1000000</c> se traduce en traerse el tenant entero a memoria.
    ///
    /// Recortar en silencio y no fallar es deliberado, igual que en <c>CustomerPaging</c>: la
    /// respuesta lleva el <c>PageSize</c> real, así que el llamador puede ver que se le recortó.
    /// </summary>
    public const int MaxPageSize = 200;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}
