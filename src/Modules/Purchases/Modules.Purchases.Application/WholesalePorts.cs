using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>
/// Una variante del mayorista como la ve el distribuidor (mayorista fase 3): precio base, descuento de su lista y precio de
/// lista, todos con IVA incluido, y la tasa de IVA del producto. El costo de la orden sale de acá (D-9).
/// </summary>
public sealed record WholesaleCatalogItem(
    Guid VariantId,
    string Code,
    string Name,
    decimal BasePrice,
    decimal DiscountPercent,
    decimal ListPrice,
    decimal TaxRatePercent,
    Guid ProductId = default,
    string ProductName = "",
    string? ImageUrl = null);

/// <summary>Una página del catálogo del mayorista con la lista que la precia (adenda del contrato: <c>priceList</c>).</summary>
public sealed record WholesaleCatalogPage(
    string Currency,
    string PriceListName,
    decimal DiscountPercent,
    IReadOnlyList<WholesaleCatalogItem> Items,
    int Total,
    bool ShowPrices = true);

/// <summary>Una categoría del mayorista que el distribuidor puede ver; <c>ParentId</c> nulo es raíz de su árbol visible.</summary>
public sealed record WholesaleCategory(Guid Id, string Name, Guid? ParentId);

/// <summary>
/// El catálogo de un mayorista vinculado; adaptador en Bootstrapper sobre Wholesale (<c>WholesaleOffers</c>). Recibe los dos
/// tenants y verifica en el servidor la relación <c>Active</c>: sin ella, 403 <c>wholesale.relationship.inactive</c>. Una
/// variante fuera de la visibilidad, inactiva, ajena o sin precio es 422 <c>wholesale.product.not_visible</c>
/// (<see cref="PurchasesErrorCodes.ProductNotVisible"/>).
/// </summary>
public interface IWholesaleCatalog
{
    /// <summary>
    /// El árbol de categorías que la lista del distribuidor le deja ver (spec 2026-11-20), plano y en el orden del mayorista.
    /// Misma verificación de relación que el catálogo: 403 <c>wholesale.relationship.inactive</c>.
    /// </summary>
    Task<IReadOnlyList<WholesaleCategory>> ListCategoriesAsync(
        Guid buyerTenantId, Guid wholesalerTenantId, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="categoryId"/> filtra por esa categoría y sus descendientes; una fuera del árbol visible es 422
    /// <c>wholesale.category.not_visible</c>.
    /// </summary>
    Task<WholesaleCatalogPage> SearchAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        string? search,
        Guid? categoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>El precio de cada variante pedida en la moneda por defecto del mayorista; todas vienen o la llamada falla. <c>ShowPrices</c>
    /// es el flag vivo de la lista del distribuidor (spec 2026-11-19): el precio viaja igual, Compras lo enmascara en su borde.</summary>
    Task<(string Currency, IReadOnlyDictionary<Guid, WholesaleCatalogItem> Items, bool ShowPrices)> PriceAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);
}

/// <summary>Una línea de la orden que viaja al pedido del mayorista: su id, la variante del mayorista y la cantidad.</summary>
public sealed record WholesaleIntakeLine(Guid PurchaseOrderLineId, Guid VariantId, decimal Quantity);

/// <summary>
/// Lo que «Enviar» entrega al mayorista. El comprador es el tenant de la ruta, nunca un dato del cuerpo. <c>Currency</c> es la
/// de la orden (D-11): si el mayorista cambió su moneda desde el borrador, el pedido no se crea ni se adopta (revisión del
/// bloque B, M2) y el distribuidor edita la orden para volver a preciarla.
/// </summary>
public sealed record WholesaleIntakeRequest(
    Guid BuyerTenantId,
    Guid WholesalerTenantId,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    string? Notes,
    IReadOnlyList<WholesaleIntakeLine> Lines,
    string Currency);

/// <summary>
/// El pedido que quedó en el mayorista con sus líneas, ya convertidas a la foto de la orden (costo sin IVA, D-9). Con
/// <c>AlreadyExisted</c> es el pedido que ya estaba (D-8) y la orden adopta esas líneas. <c>ShowPrices</c> es el flag vivo de la
/// lista del distribuidor (spec 2026-11-19, D-9): en <c>false</c> la orden adopta las líneas sin montos y los recibe al aprobar.
/// </summary>
public sealed record WholesaleIntakeResult(
    Guid OrderId,
    string OrderNumber,
    string Currency,
    IReadOnlyList<LinkedLineSnapshot> Lines,
    bool AlreadyExisted,
    bool ShowPrices = true);

/// <summary>
/// El pedido del mayorista visto desde la orden de compra; adaptador en Bootstrapper sobre Wholesale y Quotations. Cruza
/// tenants: recibe los dos y verifica la relación. Un rechazo del motor de pedidos es 409
/// <c>purchases.order.wholesaler_rejected</c> (D-15), sin el código interno del otro tenant.
/// </summary>
public interface IWholesaleOrderIntake
{
    Task<WholesaleIntakeResult> SubmitAsync(WholesaleIntakeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// D-12: anula el pedido mientras el mayorista no lo apruebe. Aprobado, preparado, facturado o archivado: 409
    /// <c>purchases.order.not_cancellable</c> (<c>ResourceConflictException</c>). Idempotente si ya estaba anulado.
    /// <para><paramref name="orderId"/> nulo es un borrador (revisión del bloque B, M1): el envío pudo crear el pedido y no
    /// alcanzar a guardarlo en la orden. Se busca el que salió de esta orden por su clave; si no hay ninguno, no pasa nada.</para>
    /// </summary>
    Task CancelAsync(
        Guid buyerTenantId,
        Guid wholesalerTenantId,
        Guid purchaseOrderId,
        string purchaseOrderNumber,
        Guid? orderId,
        CancellationToken cancellationToken);
}

/// <summary>La variante del catálogo propio que se encontró por código o se creó (D-18). <c>ProductId</c> viene sólo si se
/// creó: una variante hallada por código no se vuelve a leer y nadie lo necesita.</summary>
public sealed record PurchasesCatalogEntry(Guid VariantId, bool Created, Guid? ProductId);

/// <summary>
/// La foto de un producto que Compras acaba de crear y falta copiar del catálogo del mayorista (spec 2026-11-21): de qué
/// variante del mayorista sale y a qué producto del comprador va.
/// </summary>
public sealed record PendingProductImage(Guid BuyerTenantId, Guid SupplierTenantId, Guid SupplierVariantId, Guid ProductId);

/// <summary>
/// Copia la foto de la variante del mayorista al producto nuevo del comprador; adaptador en Bootstrapper sobre Catalog y
/// Storage. Corre **después** del commit del evento, fuera de la transacción del producto: la foto es un archivo de Storage y
/// no puede deshacerse con ella. Una falla lanza y quien llama la registra (<c>CopyPendingImagesAsync</c>): lo ya confirmado
/// no se deshace por una foto.
/// </summary>
public interface IPurchasesProductImages
{
    Task CopyAsync(PendingProductImage image, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// El catálogo del comprador para la confirmación del mayorista (D-18); adaptador en Bootstrapper sobre Catalog. Busca por
/// <paramref name="code"/> exacto (ya recortado) en variantes de cualquier estado y, si no está, crea el producto sin precio
/// de venta, sin categoría ni tasa, con costo <paramref name="unitCost"/> sin IVA sólo si es mayor que 0.
/// <para>Exige la transacción de Compras abierta (la abre el procesador del inbox, Task 7). Una falla (índice único en carrera,
/// nombre o código largo) lanza: quien llama la registra y sigue (ruling X2), así que el adaptador no puede dejar la
/// transacción abortada ni la entidad fallida en el rastreador (punto de guardado por llamada).</para>
/// </summary>
public interface IPurchasesCatalogWriter
{
    Task<PurchasesCatalogEntry> EnsureVariantAsync(
        Guid tenantId,
        string code,
        string name,
        string currency,
        decimal? unitCost,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// Una línea final del pedido aprobado (evento <c>quotations.order.wholesale-approved.v1</c>). <c>UnitPrice</c> lleva el IVA;
/// <c>SourcePurchaseOrderLineId</c> es nulo si el mayorista la agregó.
/// </summary>
public sealed record WholesaleApprovedLine(
    Guid? SourcePurchaseOrderLineId,
    Guid VariantId,
    string Code,
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxPercentage);

/// <summary>
/// El evento de aprobación ya leído por el procesador del inbox: quién lo emite y a qué orden va. <c>Currency</c> es la del
/// pedido; si no es la de la orden, la aprobación no se aplica (revisión del bloque B, M2). Nula sólo en quien arma el evento
/// sin moneda (pruebas): no se compara.
/// </summary>
public sealed record WholesaleApproval(
    Guid WholesalerTenantId,
    Guid OrderId,
    Guid BuyerTenantId,
    Guid PurchaseOrderId,
    IReadOnlyList<WholesaleApprovedLine> Lines,
    string? Currency = null);

/// <summary>El evento de anulación (<c>quotations.order.wholesale-cancelled.v1</c>) ya leído por el procesador del inbox.</summary>
public sealed record WholesaleCancellation(
    Guid WholesalerTenantId,
    Guid OrderId,
    Guid BuyerTenantId,
    Guid PurchaseOrderId);

/// <summary>
/// Lo que el procesador del inbox hace con el evento: <see cref="Applied"/> y <see cref="Ignored"/> lo marcan procesado (D-20:
/// un evento ajeno no se arregla reintentando); <see cref="RetryLater"/> no (ruling X8b: la orden sigue en borrador porque el
/// envío no terminó de guardarse).
/// </summary>
public enum WholesaleEventOutcome
{
    Applied,
    Ignored,
    RetryLater,
}
