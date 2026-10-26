using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Todo método recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca
// un argumento opcional que el llamador se pueda olvidar.
public interface IProductRepository
{
    /// <summary>
    /// Una página del listado y el total que la acompaña — mismo contrato que
    /// `ICustomerRepository.SearchAsync`. `name`/`code` son dos filtros independientes (AND
    /// cuando se llenan los dos); `search` es el criterio OR original sobre los mismos dos
    /// campos, para el combobox de productos de `quotes`. `isActive` es un filtro real de la
    /// consulta, no un post-filtro en memoria. `categoryIds`: null no filtra; no nulo deja sólo
    /// los productos de esas categorías (una colección vacía no devuelve filas). Lo usa el POS;
    /// `GET /catalog/products` no lo expone todavía (spec 2026-10-26 §3.3).
    /// </summary>
    Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        IReadOnlyCollection<Guid>? categoryIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// La misma página que <see cref="SearchAsync"/> —mismo filtro, mismo orden, mismo total—
    /// pero proyectada a lo que pinta la grilla del listado, sin traer variantes ni escalas.
    /// </summary>
    Task<(IReadOnlyList<ProductSummary> Items, int Total)> SearchSummariesAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? code,
        bool? isActive,
        IReadOnlyCollection<Guid>? categoryIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Las variantes **activas** de esos productos con lo que el selector de cotizaciones y pedidos
    /// necesita para agregar una línea (código, precios, escalas, empaques y la foto propia), en
    /// orden de producto y de <see cref="ProductVariant.Position"/>. Proyecta en la base sin cargar
    /// el agregado: una consulta de variantes, una de precios, una de escalas y, sólo si alguna
    /// variante tiene foto propia, una de galería; con 2.048 variantes por producto, el agregado
    /// de una página entera serían cientos de miles de objetos para pintar una lista. El producto
    /// activo lo decide quien llama, que ya tiene la fila.
    /// </summary>
    Task<IReadOnlyList<SellableVariantSummary>> ListActiveVariantsAsync(
        Guid tenantId,
        IReadOnlyCollection<ProductId> productIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Todos los productos que coinciden con el filtro, sin paginar y con sus variantes y las
    /// escalas de cada una (sin la galería: el Excel no la pinta) — para la
    /// exportacion, que saca el catalogo completo que el filtro deja a la vista y no la pagina
    /// que se esta mirando. Es una consulta aparte y no <see cref="SearchAsync"/> con un
    /// pageSize enorme: el listado tiene un tope de pagina a proposito, y estirarlo para que
    /// sirva de export lo convertiria en un limite que hay que recordar en dos lugares.
    ///
    /// <paramref name="skip"/>/<paramref name="take"/> son el lote con el que el handler
    /// recorre, no un tope: se lee de a tandas para no pedirle a PostgreSQL el catalogo entero
    /// de una. Mismo criterio que <c>ICustomerRepository.ListForExportAsync</c>.
    /// </summary>
    Task<IReadOnlyList<Product>> ListForExportAsync(
        Guid tenantId,
        string? name,
        string? code,
        bool? isActive,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Los mismos productos que <see cref="FindVariantsByIdsAsync"/> (cada uno con **todas** sus
    /// variantes y sus escalas), pero **con seguimiento**: quien la llama los muta y guarda por
    /// <see cref="ICatalogUnitOfWork"/>. La copia de escalas la usa con el origen y los destinos
    /// juntos, para leer el origen y escribir los destinos sobre el mismo grafo.
    ///
    /// Son dos métodos y no un booleano porque la diferencia no es un detalle de la consulta.
    /// Mutar lo que devuelve <see cref="FindVariantsByIdsAsync"/> no falla: no guarda nada, en
    /// silencio. Un parámetro se puede olvidar en la llamada; un método con otro nombre, no.
    /// </summary>
    Task<IReadOnlyList<Product>> FindVariantsByIdsForUpdateAsync(
        Guid tenantId,
        IReadOnlyCollection<VariantId> variantIds,
        CancellationToken cancellationToken);

    Task<Product?> FindAsync(
        Guid tenantId,
        ProductId productId,
        CancellationToken cancellationToken);

    /// <summary>
    /// El producto (el agregado entero, sin rastreo) que tiene la variante con ese
    /// <see cref="ProductVariant.Code"/>; quien llama elige la variante dentro de él. Devuelve el
    /// producto y no la variante suelta porque el nombre visible, la imagen principal y el IVA son
    /// del producto. Igualdad exacta y con mayúsculas, como el índice único.
    /// </summary>
    Task<Product?> FindVariantByCodeAsync(
        Guid tenantId,
        string code,
        CancellationToken cancellationToken);

    /// <summary>
    /// Como <see cref="FindVariantByCodeAsync"/>, por <see cref="ProductVariant.Barcode"/>
    /// (IX_product_variants_tenant_barcode). Igualdad exacta.
    /// </summary>
    Task<Product?> FindVariantByBarcodeAsync(
        Guid tenantId,
        string barcode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Los productos que contienen alguna de esas variantes, en una sola consulta y sin rastreo;
    /// cada uno trae **todas** sus variantes, y quien llama elige por id. Un id que no existe o es
    /// de otro tenant simplemente no aparece. La usan los adaptadores hacia Quotations y Pos para
    /// pintar las líneas de un documento sin pedir el catálogo entero.
    /// </summary>
    Task<IReadOnlyList<Product>> FindVariantsByIdsAsync(
        Guid tenantId,
        IReadOnlyCollection<VariantId> variantIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cuáles de estos códigos ya usa alguna variante del tenant, en una sola consulta — la usa la
    /// importación masiva para el chequeo de duplicados **contra la base**, mismo criterio que
    /// <c>ICustomerRepository.FindExistingIdentificationsAsync</c>: una consulta batch en vez de
    /// una por fila.
    /// </summary>
    Task<IReadOnlySet<string>> FindExistingCodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Como <see cref="FindExistingCodesAsync"/>, para los códigos de barras (igualdad exacta, como
    /// <c>IX_product_variants_tenant_barcode</c>). El PUT marca el campo antes de que choque el
    /// índice.
    /// </summary>
    Task<IReadOnlySet<string>> FindExistingBarcodesAsync(
        Guid tenantId,
        IReadOnlyCollection<string> barcodes,
        CancellationToken cancellationToken);

    void Add(Product product);

    /// <summary>
    /// Suma las filas del histórico de precios al cambio en curso. **No guarda**: como
    /// <see cref="Add"/>, deja la escritura para el <c>SaveChangesAsync</c> de
    /// <see cref="ICatalogUnitOfWork"/>, que es lo que las mantiene en la misma transacción que
    /// el producto que las originó. Un guardado propio acá dejaría histórico de un cambio que
    /// después falla al commitear.
    ///
    /// No lleva <c>tenantId</c> —única excepción entre los métodos de este puerto— porque cada
    /// <see cref="ProductPriceChange"/> ya trae el suyo, copiado del producto por
    /// <c>ProductPriceChangeDetector</c>: recibirlo aparte abriría la puerta a que difieran.
    /// </summary>
    void AddPriceChanges(IReadOnlyList<ProductPriceChange> changes);

    /// <summary>
    /// Si algún producto del tenant apunta a esa tasa. Lo pregunta `DeleteTaxRate` antes de
    /// borrar, para que el caso normal salga como un 422 que se entiende en vez de una violación
    /// de foreign key traducida.
    ///
    /// Devuelve un booleano y no la lista: quien pregunta sólo necesita saber si puede borrar, y
    /// traer los productos para contarlos sería traer datos que nadie va a mirar.
    /// </summary>
    Task<bool> AnyWithTaxRateAsync(
        Guid tenantId,
        TaxRateId taxRateId,
        CancellationToken cancellationToken);
}

/// <summary>Una fila del listado de productos (<see cref="IProductRepository.SearchSummariesAsync"/>).</summary>
/// <param name="ImageFileId">El archivo de la primera foto de la galería.</param>
/// <param name="Code">El código de la única variante; null si tiene más de una.</param>
/// <param name="PriceRanges">Por moneda, entre las variantes activas.</param>
public sealed record ProductSummary(
    ProductId Id,
    string Name,
    bool IsActive,
    string? Description,
    TaxRateId? TaxRateId,
    ProductCategoryId? CategoryId,
    Guid? ImageFileId,
    int VariantCount,
    string? Code,
    IReadOnlyList<ProductPriceRange> PriceRanges,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductPriceRange(string Currency, decimal Min, decimal Max);

/// <summary>Una variante activa como la lee el selector (<see cref="IProductRepository.ListActiveVariantsAsync"/>).</summary>
/// <param name="OptionValueIds">En el orden de las opciones del producto; vacío en la por defecto.</param>
/// <param name="Prices">Moneda → monto, sin orden garantizado: el mapeo los pone en orden de catálogo.</param>
/// <param name="Scales">Sin orden garantizado: el mapeo las ordena por <c>FromUnit</c>.</param>
/// <param name="GalleryFileId">El archivo de la foto propia de la variante; null si usa la principal.</param>
public sealed record SellableVariantSummary(
    ProductId ProductId,
    VariantId Id,
    string Code,
    string? Barcode,
    int Position,
    IReadOnlyList<Guid> OptionValueIds,
    IReadOnlyDictionary<string, decimal> Prices,
    IReadOnlyList<SellableVariantScale> Scales,
    IReadOnlyList<int> PackagingUnits,
    Guid? GalleryFileId);

public sealed record SellableVariantScale(
    Guid Id,
    int FromUnit,
    int ToUnit,
    decimal Discount,
    PriceScaleRestriction? Restriction,
    int? Multiple,
    bool AllowGrouping);
