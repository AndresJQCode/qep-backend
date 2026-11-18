namespace Modules.Purchases.Application;

/// <summary>Copia de <c>SupplierPaging</c> (D-13): «igual que <c>GET /suppliers</c>».</summary>
public static class PurchaseOrderPaging
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
