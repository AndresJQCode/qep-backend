using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Spec 2026-11-22 (listas de precios por canal, D-7/D-11): la lista de un distribuidor vive en Catalog y Mayorista la lee por
/// este puerto (lo implementa Bootstrapper). <see cref="VisibleCategoryIds"/> <c>null</c> es «todo el catálogo activo»; si no,
/// las categorías de la lista (sus descendientes también se ven).
/// </summary>
public sealed record WholesalePriceList(
    PriceListId Id,
    string Name,
    decimal AdjustmentPercent,
    IReadOnlyCollection<Guid>? VisibleCategoryIds,
    IReadOnlyCollection<Guid> HiddenCategoryIds,
    IReadOnlyCollection<Guid> HiddenProductIds,
    bool ShowPrices);

/// <summary>El precio de una variante con la lista, y si salió de un precio fijo (D-2) o del ajuste.</summary>
public readonly record struct WholesaleListedPrice(decimal Price, bool IsFixed);

/// <summary>Las listas de precios de Catalog vistas desde Mayorista. Toda lectura va acotada al tenant.</summary>
public interface IWholesalePriceLists
{
    /// <summary>La lista, o <c>null</c> si no es del tenant o ya no existe.</summary>
    Task<WholesalePriceList?> GetAsync(Guid tenantId, PriceListId id, CancellationToken cancellationToken);

    /// <summary>Todas las del tenant; el orden lo pone quien llama.</summary>
    Task<IReadOnlyList<WholesalePriceList>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// El precio de cada variante con la lista en <paramref name="currency"/>, a partir de su precio base (D-2: precio fijo o base
    /// con el ajuste y la regla de redondeo del tenant para esa moneda).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, WholesaleListedPrice>> PriceAsync(
        Guid tenantId,
        WholesalePriceList list,
        string currency,
        IReadOnlyDictionary<Guid, decimal> basePrices,
        CancellationToken cancellationToken);
}

/// <summary>Una categoría del catálogo del mayorista con su nombre.</summary>
public sealed record WholesaleCategoryRef(Guid Id, string Name);

/// <summary>
/// Spec 2026-11-20: un producto del catálogo del mayorista para elegir qué ocultar. <c>Code</c> es el de su única variante (con
/// varias, <c>null</c>); <c>VariantCount</c> todas las que tiene; <c>ImageUrl</c> la principal si el archivo es del tenant.
/// </summary>
public sealed record WholesaleProductRef(
    Guid Id, string? Code, string Name, Guid? CategoryId, int VariantCount, string? ImageUrl);

/// <summary>Un nodo del árbol plano de categorías del mayorista.</summary>
public sealed record WholesaleCategoryNode(Guid Id, string Name, Guid? ParentId);

/// <summary>Una variante vendible con su precio base en la moneda pedida y su nombre visible ya armado por el catálogo.</summary>
public sealed record WholesaleSampleVariant(Guid VariantId, string Code, string Name, decimal BasePrice);

/// <summary>
/// Fase 3: una variante que el distribuidor puede pedir (variante y producto activos, con precio en la moneda pedida), con su
/// tasa de IVA vigente y <paramref name="CategoryPath"/> = la categoría del producto y sus ancestros, para decidir la
/// visibilidad «con descendientes» sin otra lectura del árbol. Spec 2026-11-19: ProductId, ProductName e ImageUrl (la foto de la
/// variante, si no la principal del producto, si no null; sólo si el archivo es del tenant y está disponible) para que el
/// distribuidor dibuje tarjetas por producto.
/// </summary>
public sealed record WholesaleSellableVariant(
    Guid VariantId,
    string Code,
    string Name,
    decimal BasePrice,
    decimal TaxRatePercent,
    IReadOnlyList<Guid> CategoryPath,
    Guid ProductId = default,
    string ProductName = "",
    string? ImageUrl = null);

/// <summary>El catálogo del mayorista visto desde Mayorista; adaptador en Bootstrapper sobre Catalog. Todo acotado al tenant.</summary>
public interface IWholesaleCatalogReader
{
    /// <summary>En lote: un id que no es una categoría del tenant (ajena o borrada) no viene.</summary>
    Task<IReadOnlyDictionary<Guid, WholesaleCategoryRef>> FindCategoriesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-20: en lote, los productos del tenant con esos ids (activos o no); uno ajeno o borrado no viene.</summary>
    Task<IReadOnlyDictionary<Guid, WholesaleProductRef>> FindProductsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Spec 2026-11-20, el selector de productos a ocultar: una página de productos activos con precio en
    /// <paramref name="currency"/>, por nombre; <c>Total</c> cuenta productos. <paramref name="categoryId"/> filtra con
    /// descendientes; <paramref name="search"/> busca en código o nombre.
    /// </summary>
    Task<(IReadOnlyList<WholesaleProductRef> Items, int Total)> SearchProductsAsync(
        Guid tenantId, string currency, Guid? categoryId, string? search, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Todas las categorías del tenant como árbol plano, ordenadas por nombre (ruling W5).</summary>
    Task<IReadOnlyList<WholesaleCategoryNode>> ListCategoriesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Hasta <paramref name="take"/> variantes vendibles con precio en <paramref name="currency"/>, ordenadas por nombre de
    /// producto y posición (D-11), y cuántos productos activos entran en el filtro. <paramref name="categoryIds"/> <c>null</c> es
    /// todo el catálogo activo; si no, esas categorías y sus descendientes (D-9). Spec 2026-11-20: los productos de
    /// <paramref name="excludedCategoryIds"/> (con sus descendientes) y <paramref name="excludedProductIds"/> no entran, ni en la
    /// muestra ni en el total; <c>null</c> o vacía no excluye nada.
    /// </summary>
    Task<(IReadOnlyList<WholesaleSampleVariant> Items, int TotalProducts)> SampleAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Guid>? excludedCategoryIds,
        IReadOnlyCollection<Guid>? excludedProductIds,
        int take,
        CancellationToken cancellationToken);

    /// <summary>En lote: un id inactivo, de producto inactivo, sin precio en <paramref name="currency"/> o ajeno no viene.</summary>
    Task<IReadOnlyDictionary<Guid, WholesaleSellableVariant>> FindSellableAsync(
        Guid tenantId, string currency, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de productos activos (nombre de producto y posición) con sus variantes vendibles en
    /// <paramref name="currency"/>; <paramref name="search"/> busca en código o nombre. <paramref name="categoryIds"/> como en
    /// <see cref="SampleAsync"/>, y las exclusiones también (en la consulta, así <c>Total</c> y la página son verdad).
    /// <c>Total</c> cuenta productos: la página es de productos, como el selector de cotizaciones.
    /// </summary>
    Task<(IReadOnlyList<WholesaleSellableVariant> Items, int Total)> SearchSellableAsync(
        Guid tenantId,
        string currency,
        IReadOnlyCollection<Guid>? categoryIds,
        IReadOnlyCollection<Guid>? excludedCategoryIds,
        IReadOnlyCollection<Guid>? excludedProductIds,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

/// <summary>Guarda los cambios del módulo con su auditoría y su outbox. Traduce los índices únicos por nombre a 409.</summary>
public interface IWholesaleUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Suelta lo rastreado sin guardar. Después de perder una carrera al guardar (412 o un índice único), la relectura tiene
    /// que traer la fila de la base y no la copia modificada en memoria, ni reenviar la auditoría que chocó.
    /// </summary>
    void DiscardPendingChanges();
}

/// <summary>
/// Auditoría como puerto propio (copia de <c>IPurchasesAuditPublisher</c>): el adaptador acumula la entrada en el contexto del
/// módulo para que se confirme o se revierta con el cambio de negocio (D-24).
/// </summary>
public interface IWholesaleAuditPublisher
{
    void Publish(
        Guid tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

/// <summary>
/// Los distribuidores del mayorista. Toda lectura va acotada al tenant salvo <see cref="FindByTokenHashAsync"/>: el hash es la
/// llave y quien abre el link no tiene tenant.
/// </summary>
public interface IDistributorRepository
{
    Task<Distributor?> GetAsync(Guid tenantId, DistributorId id, CancellationToken cancellationToken);

    /// <summary>Por el SHA-256 del token (nunca el token plano). Un token rotado o un distribuidor borrado dan <c>null</c>.</summary>
    Task<Distributor?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Si el documento ya es de otro distribuidor del tenant, en cualquier estado (D-13). Al editar,
    /// <paramref name="excluding"/> es el propio distribuidor; al invitar, <c>null</c>.</summary>
    Task<bool> IdentificationExistsAsync(
        Guid tenantId,
        string identificationType,
        string identificationNumber,
        DistributorId? excluding,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fase 3: la relación entre los dos tenants, en cualquier estado, o <c>null</c>. Va acotada al mayorista: quien pregunta
    /// por otro par no encuentra nada.
    /// </summary>
    Task<Distributor?> FindByTenantsAsync(
        Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken cancellationToken);

    /// <summary>Si el tenant del distribuidor ya tiene relación con este mayorista.</summary>
    Task<bool> IsLinkedAsync(Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken cancellationToken);

    /// <summary>Los tenants distribuidores ya vinculados al mayorista, en una consulta.</summary>
    Task<IReadOnlySet<Guid>> LinkedTenantIdsAsync(Guid wholesalerTenantId, CancellationToken cancellationToken);

    /// <summary>Una página ordenada por <c>created_at DESC, id DESC</c>. <paramref name="search"/> busca en razón social,
    /// documento o correo.</summary>
    Task<(IReadOnlyList<Distributor> Items, int Total)> SearchAsync(
        Guid tenantId,
        DistributorStatus? status,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Cuántos hay por estado con <paramref name="search"/> y sin filtro de estado. Un estado sin filas no viene.</summary>
    Task<IReadOnlyDictionary<DistributorStatus, int>> CountByStatusAsync(
        Guid tenantId, string? search, CancellationToken cancellationToken);

    void Add(Distributor distributor);

    void Remove(Distributor distributor);
}

/// <summary>
/// El evento <c>wholesale.distributor-invited.v1</c> hacia el correo y los otros eventos del módulo. El token plano sólo viaja en
/// el del correo y en el del aviso con link; la prueba de buzón (spec 2026-11-24), sólo en el del correo. El adaptador los
/// escribe en el outbox del contexto del módulo, así que salen con el mismo commit que la invitación.
/// </summary>
public interface IWholesaleEventPublisher
{
    void DistributorInvited(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        string emailProof,
        DateTimeOffset expiresAt,
        string correlationId,
        DateTimeOffset occurredAt);

    /// <summary>
    /// Spec 2026-11-19 §2: <c>wholesale.distributor-invitation-link.v1</c>, el aviso por WhatsApp de la invitación con su link.
    /// Reemplaza a <c>wholesale.distributor-invitation-notice.v1</c> (sin link), que ya no se escribe y sólo se drena. Sale en el
    /// mismo commit que la invitación y lo procesa el worker del aviso. <paramref name="token"/> es el mismo del correo y sólo
    /// vive en el payload del outbox; <paramref name="generation"/> es la de la invitación al encolarlo (D-4). No lleva la prueba
    /// de buzón: el link de WhatsApp no demuestra nada sobre el correo (spec 2026-11-24).
    /// </summary>
    void InvitationLinkRequested(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        string token,
        int generation,
        string correlationId,
        DateTimeOffset occurredAt);

    /// <summary>
    /// Spec 2026-11-19 §6: <c>wholesale.distributor-email-changed.v1</c>. Al aceptar quedó el correo de la cuenta en lugar del que
    /// escribió el mayorista; Notifications avisa a las dos direcciones. Sale en el mismo commit que la aceptación. Sin token.
    /// </summary>
    void DistributorEmailChanged(
        Guid tenantId,
        Guid distributorId,
        string previousEmail,
        string newEmail,
        string businessName,
        string correlationId,
        DateTimeOffset now);
}

/// <summary>
/// Spec 2026-11-19 §1: el link de la invitación que se le devuelve al mayorista para «Copiar». Adaptador en Bootstrapper con
/// <c>AccountLinks.WholesaleInvitation</c> (<c>Notifications:AppBaseUrl</c>, ruling W1): el mismo link que lleva el correo.
/// </summary>
public interface IWholesaleInvitationLinks
{
    string Compose(string token);
}

/// <summary>
/// Una empresa facturadora del mayorista con lo que el proveedor vinculado necesita (D-2). <c>DocumentType</c> va con el
/// nombre del cable; <c>Eligible</c> dice si ese documento lo admite un proveedor (hoy los siete códigos: no hay
/// documento que deje a una empresa fuera).
/// </summary>
public sealed record WholesaleCompanyRef(
    Guid Id,
    string Name,
    string DocumentType,
    string DocumentNumber,
    string Country,
    Guid? CityId,
    string? CityName,
    string? Phone,
    string? Email,
    string? Address,
    bool IsActive,
    bool Eligible);

/// <summary>Las empresas del mayorista; adaptador en Bootstrapper sobre Companies. Todo acotado al tenant.</summary>
public interface IWholesaleCompanyLookup
{
    Task<WholesaleCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WholesaleCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una ciudad de Geography con su departamento: Wholesale la valida y la nombra, y el detalle necesita el
/// departamento para que la edición preseleccione la cascada.</summary>
public sealed record WholesaleCityRef(Guid Id, string Name, Guid DepartmentId, string? DepartmentName);

/// <summary>Las ciudades de Colombia; adaptador en Bootstrapper sobre Geography. Sirve para rechazar al invitar una ciudad
/// que después haría fallar cada aceptación y para nombrarla en el detalle.</summary>
public interface IWholesaleCityLookup
{
    Task<WholesaleCityRef?> FindAsync(Guid cityId, CancellationToken cancellationToken);
}

/// <summary>Una membresía activa con su nombre visible.</summary>
public sealed record WholesaleMember(Guid MemberId, string Name);

/// <summary>El padrón del mayorista; adaptador en Bootstrapper sobre Tenancy.</summary>
public interface IWholesaleMemberDirectory
{
    /// <summary>Las membresías activas del tenant: quien puede ser asesor (D-15, sin filtro de rol).</summary>
    Task<IReadOnlyList<WholesaleMember>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Nombres visibles en cualquier estado; una membresía sin nombre o que no existe no viene.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}

/// <summary>
/// El nombre vigente de los tenants distribuidores, en lote (la lista pinta una página entera). Adaptador en Bootstrapper
/// sobre Tenancy; <c>ITenantDirectory</c> sólo lee de a uno.
/// </summary>
public interface IWholesaleTenantDirectory
{
    Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);
}

/// <summary>
/// El proveedor vinculado en el tenant del distribuidor (D-2, D-8); adaptador sobre Suppliers. Idempotente: por vínculo, por
/// documento o nuevo. Devuelve el id del proveedor.
/// </summary>
public interface IWholesaleSupplierLinker
{
    Task<Guid> EnsureLinkedSupplierAsync(
        Guid distributorTenantId,
        Guid wholesalerTenantId,
        string wholesalerName,
        WholesaleCompanyRef company,
        Guid actorUserId,
        CancellationToken cancellationToken);
}

/// <summary>
/// El cliente del distribuidor en el tenant del mayorista (D-7); adaptador sobre Customers. Idempotente por documento: si ya
/// existe se reutiliza, sin actualizarlo. Devuelve el id del cliente. Ciudad, dirección y nombre del contacto del distribuidor
/// también van al cliente (decisión del owner, 2026-11-24): la ciudad y la dirección a la ficha, el contacto como etiqueta de la
/// dirección principal de la libreta.
/// </summary>
public interface IWholesaleCustomerProvisioning
{
    Task<Guid> EnsureCustomerAsync(
        Guid wholesalerTenantId,
        Guid actorUserId,
        string businessName,
        string country,
        string identificationType,
        string identificationNumber,
        string email,
        string? phone,
        Guid? cityId,
        string? cityName,
        string? address,
        string contactName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Los datos de la empresa del distribuidor tal como los escribió el mayorista al invitar. Wholesale no conoce
/// <c>Companies</c>: el adaptador de Bootstrapper los traduce.
/// </summary>
public sealed record WholesaleCompanyDraft(
    string Name,
    string Country,
    string DocumentType,
    string TaxId,
    Guid? CityId,
    string? CityName,
    string? Address,
    string? Email,
    string? Phone);

/// <summary>
/// La empresa del distribuidor en su tenant nuevo (spec 2026-11-23 §3); adaptador sobre Companies. Idempotente por tipo y
/// número de documento exactos: un reintento devuelve la misma empresa. Las excepciones de Companies salen tal cual.
/// </summary>
public interface IWholesaleCompanyProvisioning
{
    Task<Guid> EnsureCompanyAsync(
        Guid distributorTenantId,
        Guid actorUserId,
        WholesaleCompanyDraft draft,
        CancellationToken cancellationToken);

    /// <summary>
    /// Para un tenant que ya existía (el distribuidor vinculó uno suyo): crea la empresa sólo si el tenant no tiene ninguna. Devuelve
    /// <c>null</c> cuando ya tenía alguna y no se escribió nada.
    /// </summary>
    Task<Guid?> EnsureFirstCompanyAsync(
        Guid distributorTenantId,
        Guid actorUserId,
        WholesaleCompanyDraft draft,
        CancellationToken cancellationToken);
}
