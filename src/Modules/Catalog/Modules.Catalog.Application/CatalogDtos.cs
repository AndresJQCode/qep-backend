namespace Modules.Catalog.Application;

// Variantes, Tarea 5 (spec 2026-10-20 §6). El detalle es lo que el formulario dibuja y lo que
// vuelve del PUT, entero, para que lo repinte sin otra lectura.
//
// Decisiones de forma (BFF; el backend no ve la pantalla que las pidió):
// - Code, precios, escalas y empaques ya no están arriba: son de cada variante. Un producto sin
//   opciones trae Variants con **una** sola, y el formulario la pinta como los campos de siempre
//   (spec §3.2). Un campo de arriba que valiera «el de la única variante» obligaría a la pantalla a
//   saber cuándo vale y cuándo no.
// - ImageFileId e ImageUrl se quedan: son la imagen principal (la primera de Gallery), y la grilla,
//   la cotización y el POS la pintan sin recorrer la galería.
// - Version viaja para que el PUT la devuelva como ExpectedVersion: sin ella, dos personas
//   editando el mismo producto se pisan en silencio (gana el último).
public sealed record ProductDto(
    Guid Id,
    string Name,
    bool IsActive,
    string? Description,
    Guid? ImageFileId,
    string? ImageUrl,
    Guid? TaxRateId,
    IReadOnlyList<ProductOptionResponse> Options,
    IReadOnlyList<ProductVariantResponse> Variants,
    IReadOnlyList<ProductGalleryImageResponse> Gallery,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProductResponse(
    Guid Id,
    string Name,
    bool IsActive,
    string? Description,
    // La imagen principal: el archivo de la primera foto de Gallery. Derivada; se cambia
    // reordenando Gallery en el PUT.
    Guid? ImageFileId,
    // CAT-05b. Derivado y de sólo lectura: dónde se sirve el archivo es de Storage, no de
    // catalog. Viene en null si la imagen no fue publicada o es de otro tenant.
    string? ImageUrl,
    Guid? TaxRateId,
    // Las opciones del producto en su orden, con sus nombres ya resueltos de la biblioteca.
    IReadOnlyList<ProductOptionResponse> Options,
    // **Todas** las variantes en orden de presentación, activas e inactivas: el formulario edita
    // también las inactivas, y el PUT exige que vuelvan todas las guardadas salvo las que pide
    // eliminar en DeletedVariantIds (spec §3.3, §6); cada una trae CanDelete para anticiparlo.
    IReadOnlyList<ProductVariantResponse> Variants,
    // La galería en orden; la primera es la principal.
    IReadOnlyList<ProductGalleryImageResponse> Gallery,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Una opción del producto. <paramref name="Values"/> son los valores que usa alguna variante de
/// este producto, en el orden de la biblioteca —no la biblioteca entera, que el formulario lee de
/// <c>/catalog/options</c>—: es lo que la pantalla necesita para dibujar los encabezados de la
/// tabla de variantes sin cruzar ids. Mismo <c>{ id, name }</c> que <c>OptionValueResponse</c>.
/// </summary>
public sealed record ProductOptionResponse(
    Guid Id,
    string Name,
    IReadOnlyList<ProductOptionValueResponse> Values);

public sealed record ProductOptionValueResponse(Guid Id, string Name);

/// <param name="Name">El nombre visible ya armado (spec §3.4: «Camiseta – M / Azul»; el del
/// producto en la variante por defecto). El frontend no concatena: es el mismo texto que guardan
/// las líneas, el PDF y el POS, y armarlo en dos lugares los dejaría distintos.</param>
/// <param name="OptionValueIds">Un valor por opción, en el orden de <c>Options</c>; vacío en la
/// variante por defecto.</param>
/// <param name="Prices">Igual que el antiguo <c>prices</c> del producto: en orden de catálogo.</param>
/// <param name="PriceScales">Ordenadas por <c>FromUnit</c>, con los finales de cada moneda.</param>
/// <param name="PackagingUnits">Viajan siempre, vacíos incluidos: el formulario los repinta tal cual.</param>
/// <param name="GalleryImageId">La fila de galería que muestra la variante.</param>
/// <param name="GalleryFileId">El archivo de esa foto: es lo que el PUT pide de vuelta
/// (<c>ProductVariantRequest.GalleryFileId</c>), así el formulario no cruza Gallery para
/// reenviarlo.</param>
/// <param name="ImageUrl">La URL que la variante muestra: la de su foto o, sin foto, la de la
/// principal del producto (spec §3.3). Resuelta acá para que la pantalla no repita la regla.</param>
/// <param name="CanDelete">Si el formulario puede ofrecer «Eliminar» en vez de «Desactivar»
/// (spec §3.3): true cuando ninguna línea de cotización, pedido o venta del POS guarda esta
/// variante. Viaja calculado porque desde la pantalla no se ven esas líneas. «Eliminar» es mandar
/// su id en <c>UpdateProductRequest.DeletedVariantIds</c> (omitirla de <c>Variants</c> no borra:
/// responde <c>missing_in_request</c>): el backend la borra si sigue sin uso y la desactiva si,
/// entretanto, alguien la vendió (la respuesta lo muestra). Con false, pedir su borrado la
/// desactiva. No mira la regla de «al menos una variante activa»: esa la responde el PUT con su
/// propio código.</param>
public sealed record ProductVariantResponse(
    Guid Id,
    string Name,
    string Code,
    string? Barcode,
    IReadOnlyList<Guid> OptionValueIds,
    IReadOnlyDictionary<string, decimal> Prices,
    IReadOnlyCollection<PriceScaleResponse> PriceScales,
    IReadOnlyList<int> PackagingUnits,
    Guid? GalleryImageId,
    Guid? GalleryFileId,
    string? ImageUrl,
    bool IsActive,
    bool CanDelete);

/// <param name="Url">Null si el archivo no está publicado o no es del tenant, igual que
/// <c>ImageUrl</c>; <c>FileId</c> viaja siempre porque es lo que el PUT manda de vuelta.</param>
public sealed record ProductGalleryImageResponse(Guid Id, Guid FileId, string? Url);

/// <summary>
/// Una fila del listado de productos. El listado sigue siendo por producto (spec §6), así que no
/// trae las variantes: trae lo que la grilla pinta de ellas.
/// </summary>
/// <param name="Code">El código de la única variante; null cuando el producto tiene varias
/// (<paramref name="VariantCount"/> lo explica). La grilla de un catálogo sin variantes se ve como
/// antes, y la de uno con variantes no inventa un código que no es de nadie.</param>
/// <param name="VariantCount">Todas las variantes, activas e inactivas.</param>
/// <param name="PriceRange">Por moneda, el menor y el mayor precio entre las variantes
/// **activas**, en orden de catálogo: con 2.048 variantes posibles, sumar o recorrer del lado del
/// cliente no es opción. Una sola variante da Min = Max.</param>
public sealed record ProductListItemDto(
    Guid Id,
    string Name,
    string? Code,
    bool IsActive,
    string? Description,
    Guid? ImageFileId,
    string? ImageUrl,
    Guid? TaxRateId,
    int VariantCount,
    IReadOnlyDictionary<string, PriceRangeResponse> PriceRange,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PriceRangeResponse(decimal Min, decimal Max);

/// <summary>
/// Precio y escalas de un producto (CAT-09), tal como los manda el cliente.
/// </summary>
/// <param name="Prices">At least one entry (the domain answers <c>catalog.product.price_required</c>).
/// Keys are catalogue codes; an unknown one is a 422 on <c>errors["Pricing.Prices.&lt;CODE&gt;"]</c>.
/// baseUsd/baseCop from the previous contract are ignored if present.</param>
/// <param name="PackagingUnits">Los empaques del producto (p. ej. <c>[100, 150]</c>). Viajan en
/// el precio y no en los datos maestros porque una escala <c>packaging_unit</c> se valida
/// contra ellos: el dominio los revisa juntos, y un empaque que llegara por otro request dejaría
/// una ventana con escalas que exigen empaque y ninguno cargado. Null es lo mismo que vacío,
/// para que un cuerpo que no los manda siga siendo válido mientras no tenga escalas de empaque.</param>
public sealed record ProductPricingRequest(
    IReadOnlyDictionary<string, decimal>? Prices,
    IReadOnlyCollection<PriceScaleRequest>? Scales,
    IReadOnlyCollection<int>? PackagingUnits);

/// <summary>
/// Restriction es texto ("multiple" | "packaging_unit") y no el enum del dominio: ningún DTO
/// expone <c>PriceScaleRestriction</c> directamente, mismo criterio que
/// <c>MembershipListItemResponse.State</c>.
///
/// No lleva unidad de empaque: una escala <c>packaging_unit</c> usa los empaques del producto
/// (<see cref="ProductPricingRequest.PackagingUnits"/>).
///
/// No finals: they are derived from each product price and the discount (spec D4).
/// finalUsd/finalCop sent by an old client are ignored, not rejected.
/// </summary>
/// <param name="AllowGrouping">Nullable y último: los cuerpos que no lo mandan mantienen el
/// comportamiento de siempre, que es no agrupar.</param>
public sealed record PriceScaleRequest(
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string? Restriction,
    int? Multiple,
    bool? AllowGrouping);

/// <param name="Restriction">Null en una escala incompleta, la que deja la copia de escalas
/// (<c>PriceScaleCopy</c>). No hay un campo aparte que lo diga: el frontend lo deriva de este null,
/// y un segundo campo sólo abriría la puerta a que los dos se contradigan.</param>
/// <param name="Finals">Derived with <c>PriceScale.FinalFor</c> for each currency the product has,
/// in catalogue order. Read-only.</param>
public sealed record PriceScaleResponse(
    Guid Id,
    int FromUnit,
    int ToUnit,
    decimal Discount,
    string? Restriction,
    int? Multiple,
    bool AllowGrouping,
    IReadOnlyDictionary<string, decimal> Finals);

/// <summary>El sobre del listado, con el total que la paginación necesita — mismo criterio que
/// `CustomersResponse`.</summary>
/// <summary>
/// La respuesta del 202 de exportacion. No lleva el enlace a proposito: el archivo llega por
/// correo, y devolverlo tambien aca duplicaria el canal de entrega.
/// </summary>
public sealed record ProductExportResponse(
    string FileName,
    int ProductCount,
    DateTimeOffset ExpiresAt);

public sealed record ProductsResponse(
    IReadOnlyCollection<ProductListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

// IsActive del producto no viaja en los requests: un producto nace activo y sólo cambia por
// /deactivate. Un booleano editable convertiría la desactivación en un PUT común y la dejaría sin
// su propia entrada de auditoría, el mismo razonamiento que mantuvo suspender aparte de editar
// roles en AUTH-06. El IsActive de cada **variante** sí viaja (spec §3.3): es parte de la tabla
// de variantes que el formulario edita.
//
// Description/TaxRateId son opcionales, y en el PUT mandarlos en null los **limpia** — el verbo
// reemplaza el recurso entero. Lo mismo OptionIds y Gallery: null es lista vacía.
//
// Variantes, Tarea 5: Code y Pricing salieron del producto hacia cada variante, y ImageFileId hacia
// Gallery (la primera foto es la principal). Un producto sin opciones manda una sola variante, sin
// OptionValueIds.
public sealed record CreateProductRequest(
    string Name,
    string? Description,
    Guid? TaxRateId,
    IReadOnlyList<Guid>? OptionIds,
    IReadOnlyList<ProductVariantRequest>? Variants,
    IReadOnlyList<Guid>? Gallery);

/// <summary>
/// Una variante como la manda el formulario. En el PUT se empareja por <paramref name="Id"/>
/// (spec §3.3.1): con id es esa variante guardada; sin id, una nueva.
/// </summary>
/// <param name="OptionValueIds">Un valor por opción, en el orden de <c>OptionIds</c>. En el PUT,
/// null en una variante guardada significa «los suyos»: se proyectan a las opciones nuevas —una
/// opción que se quita suelta su valor, una que se agrega toma su primer valor activo—, así quitar
/// o agregar una opción no obliga a la pantalla a reescribir cada fila.</param>
/// <param name="Pricing">Obligatorio en el POST. En el PUT, null en una variante guardada deja su
/// precio, escalas y empaques como están; en una nueva los copia de la variante guardada que más
/// valores comparte con ella (spec §3.3.1: «copiadas de la variante de su misma talla»).</param>
/// <param name="GalleryFileId">La foto de la variante, por id de archivo: una foto que se sube en el
/// mismo PUT todavía no tiene fila de galería. Tiene que estar en <c>Gallery</c>.</param>
/// <param name="IsActive">Null es activa en una variante nueva y «como está» en una guardada.</param>
public sealed record ProductVariantRequest(
    Guid? Id,
    string? Code,
    string? Barcode,
    IReadOnlyList<Guid>? OptionValueIds,
    ProductPricingRequest? Pricing,
    Guid? GalleryFileId,
    bool? IsActive);

/// <summary>
/// El cuerpo de la copia de escalas: de dónde salen y a quiénes van. No lleva las escalas
/// mismas a propósito — el backend las lee del origen. Que las mandara el cliente es lo que
/// permitía copiar algo distinto de lo que la pantalla mostró.
/// </summary>
public sealed record CopyPriceScalesRequest(
    Guid SourceProductId,
    IReadOnlyList<Guid> TargetProductIds);

/// <summary>
/// Los destinos ya actualizados, en el orden en que se pidieron. Devolverlos evita que el
/// cliente tenga que releer el listado para pintar el resultado.
/// </summary>
public sealed record CopyPriceScalesResponse(
    IReadOnlyCollection<ProductResponse> Products);

/// <param name="ExpectedVersion">La <c>Version</c> que el formulario leyó. Si el producto cambió
/// desde entonces, el PUT responde 412 <c>concurrency.conflict</c> en vez de pisar lo que guardó
/// otra persona. Mismo nombre que el resto de los PUT del repositorio (notas, etiquetas,
/// cotizaciones).</param>
/// <param name="DeletedVariantIds">Las variantes guardadas que el usuario pidió eliminar (spec §3.3 y
/// §6). Se borran si ninguna línea de cotización, pedido o venta del POS las guarda y se desactivan
/// si alguna sí (la respuesta las trae inactivas). Una guardada que no viene en
/// <c>Variants</c> ni acá responde 422 <c>catalog.variant.missing_in_request</c>: **omitir no
/// borra**. Es BFF a propósito: la tabla de variantes llega a 2.048 filas y el formulario la
/// pagina o virtualiza, así que una fila ausente puede ser una página que no se mandó, no una orden
/// de borrar. Un id que además viene en <c>Variants</c> da <c>duplicated_in_request</c>; uno que no
/// es de este producto, <c>catalog.variant.not_found</c>.</param>
public sealed record UpdateProductRequest(
    long ExpectedVersion,
    string Name,
    string? Description,
    Guid? TaxRateId,
    IReadOnlyList<Guid>? OptionIds,
    IReadOnlyList<ProductVariantRequest>? Variants,
    IReadOnlyList<Guid>? Gallery,
    IReadOnlyList<Guid>? DeletedVariantIds = null);

public sealed record TaxRateDto(
    Guid Id,
    string Name,
    int Percentage,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaxRateResponse(
    Guid Id,
    string Name,
    int Percentage,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TaxRatesResponse(IReadOnlyCollection<TaxRateResponse> Items);

// Percentage es int, no decimal: P-008, decidido por el owner el 2026-08-10, fija el porcentaje
// en 0 decimales. Encaja con el IVA colombiano —19, 5 o 0— y el gate CAT-00 lo declara como
// límite de alcance del módulo, o sea que no admite retenciones con fracción.
//
// IsActive tampoco viaja acá, por la misma razón que en producto.
public sealed record CreateTaxRateRequest(string Name, int Percentage);

public sealed record UpdateTaxRateRequest(string Name, int Percentage);
