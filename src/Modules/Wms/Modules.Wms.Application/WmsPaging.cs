namespace Modules.Wms.Application;

/// <summary>Copia de <c>ReportPaging</c>: el tamaño de página lo elige el cliente, así que sin tope un <c>pageSize</c> enorme se
/// traduce en traer el tenant entero. Se recorta en silencio y la respuesta lleva el tamaño real.</summary>
public static class WmsPaging
{
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 200;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize,
    };
}
