namespace Modules.Wholesale.Application;

/// <summary>Copia de <c>PurchaseOrderPaging</c> (D-22): 50 por defecto, tope 200.</summary>
public static class DistributorPaging
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Tope duro: sin él un <c>?pageSize=1000000</c> trae el tenant entero a memoria. Se recorta en silencio y la respuesta lleva
    /// el <c>PageSize</c> real.
    /// </summary>
    public const int MaxPageSize = 200;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize,
    };
}
