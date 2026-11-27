using Modules.Siigo.Domain;

namespace Modules.Siigo.Application;

// Spec 2026-11-18 §5.1: los puertos de Siigo hacia los demás módulos. Siigo sólo ve Tenancy y Audit; conexión, empresa, impuestos,
// catálogo, pedido y cliente entran por estos puertos y su adaptador vive en src/Bootstrapper/Siigo*.cs (SiigoLayerTests). Este archivo
// guarda los de la configuración (Tarea 6); los de productos, pedido y cliente llegan con sus tareas.

/// <summary>Una conexión de Siigo del tenant, con su estado en Integrations (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>).</summary>
public sealed record SiigoConnectionInfo(Guid Id, string Name, string Status);

/// <summary>
/// El resultado de resolver una conexión: sus credenciales ya descifradas, o por qué no se puede usar (<c>not_found</c> también para la
/// conexión de otro proveedor, <c>paused</c>, <c>needs_attention</c>, <c>provider_hidden</c>, <c>provider_disabled</c>,
/// <c>secret_unreadable</c>, <c>incomplete</c>). <see cref="Credentials"/> no es <c>null</c> sólo si <see cref="UnavailableReason"/> lo es.
/// </summary>
public sealed record SiigoConnectionResolution(SiigoCredentials? Credentials, string? UnavailableReason);

/// <summary>Las conexiones de Siigo de Integrations. El adaptador usa <c>IIntegrationConnections</c> (como <c>AiProviderCredentials</c>);
/// Siigo no referencia Integrations.</summary>
public interface ISiigoConnectionDirectory
{
    /// <summary>La conexión de Siigo <b>del tenant</b>, en cualquier estado; <c>null</c> si no existe, es de otro tenant o es de otro
    /// proveedor (el llamador responde 403, nunca 404).</summary>
    Task<SiigoConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las credenciales para <b>operar</b> (facturar, sellar): sólo una conexión <c>Active</c>.</summary>
    Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las credenciales para <b>configurar</b> (listas del asistente, productos y sus vínculos): también una <c>Paused</c>.
    /// Pausar es dejar de facturar, no dejar de configurar.</summary>
    Task<SiigoConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todas las del tenant, por nombre.</summary>
    Task<IReadOnlyList<SiigoConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>La empresa de QEP que factura con la conexión.</summary>
public sealed record SiigoCompanyRef(Guid Id, string Name, bool IsActive);

/// <summary>Una cuenta de cobro de la empresa. <paramref name="Key"/> es <see cref="BankAccountKey.Normalize"/> de banco y número.</summary>
public sealed record SiigoBillingAccountRef(string BankName, string AccountNumber, string Key);

/// <summary>Companies. Lee la misma fuente de la que <c>QuotationBillingAccount</c> toma su foto (las cuentas de la empresa).</summary>
public interface ISiigoCompanyDirectory
{
    Task<SiigoCompanyRef?> FindCompanyAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SiigoBillingAccountRef>> ListBillingAccountsAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}

/// <summary>Una retención del catálogo de impuestos que la empresa tiene asignada.</summary>
public sealed record SiigoWithholdingRef(Guid TaxRateId, string Name, string Type, decimal Percentage);

/// <summary>Catalog y Companies. Las tasas que el paso «Impuestos y retenciones» ofrece vincular.</summary>
/// <summary>Un miembro del tenant que puede ser asesor de un pedido. <paramref name="Active"/> es false para una membresía que ya no está
/// activa pero conserva su vínculo de vendedor.</summary>
public sealed record SiigoMemberRef(Guid MembershipId, string? DisplayName, string? Email, bool Active);

/// <summary>
/// Spec 2026-11-20 (vendedores vinculados) §4.1: los miembros que el paso «Vendedores» ofrece. Todas las membresías activas del tenant,
/// sin filtro de rol (el asesor de un pedido puede ser cualquier miembro), más las de <c>alsoInclude</c> (las que ya tienen vínculo)
/// aunque no estén activas. Nunca una de otro tenant. El adaptador vive en Bootstrapper: Siigo no lee Tenancy ni Identity.
/// </summary>
public interface ISiigoMemberDirectory
{
    Task<IReadOnlyList<SiigoMemberRef>> ListAsync(Guid tenantId, IReadOnlyCollection<Guid> alsoInclude, CancellationToken cancellationToken);
}

public interface ISiigoTaxRateDirectory
{
    /// <summary>Los porcentajes de IVA (enteros) de las tasas que usan los productos activos del tenant. Incluye siempre el 0.</summary>
    Task<IReadOnlyList<int>> ListVatPercentagesInUseAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Las retenciones activas que la empresa tiene asignadas, por nombre y porcentaje.</summary>
    Task<IReadOnlyList<SiigoWithholdingRef>> ListWithholdingRatesAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}

/// <summary>El repositorio de la configuración de Siigo. Con tracking: el llamador modifica y guarda con <see cref="ISiigoUnitOfWork"/>.
/// Todo filtra por <c>tenant_id</c>.</summary>
public interface ISiigoSettingsRepository
{
    /// <summary>
    /// La configuración de la conexión; si el módulo todavía no la tiene, la crea vacía con <c>INSERT … ON CONFLICT DO NOTHING</c> (dos
    /// primeros accesos a la vez no chocan: ninguno falla y los dos leen la misma fila). <c>null</c> sólo si la fila existe con otro
    /// tenant. Trae los vínculos de impuestos, retenciones y cuentas; los de productos sólo con <paramref name="withVariantLinks"/>.
    /// </summary>
    Task<SiigoSettings?> FindOrCreateAsync(Guid tenantId, Guid connectionId, bool withVariantLinks, CancellationToken cancellationToken);

    /// <summary>
    /// La configuración de la conexión que factura con esa empresa (<c>IX_settings_tenant_company</c>: una por tenant), o <c>null</c> si
    /// ninguna la eligió. Trae los vínculos de impuestos, retenciones y cuentas, y los de productos <b>sólo de
    /// <paramref name="variantIds"/></b>: la factura los necesita de las líneas del pedido y la tabla llega a miles de filas. Es de lectura:
    /// la factura no modifica la configuración.
    /// </summary>
    Task<SiigoSettings?> FindByCompanyAsync(
        Guid tenantId, Guid companyId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Marks the row for deletion on the next save; its links go with it (cascade). Only for settings whose connection no longer
    /// exists: <c>siigo.settings</c> has no FK to Integrations, so deleting a connection leaves its row behind.</summary>
    void Remove(SiigoSettings settings);
}

/// <summary>Las facturas de Siigo (<c>siigo.invoices</c>). Con tracking: el llamador modifica y guarda con <see cref="ISiigoUnitOfWork"/>.
/// Todo filtra por <c>tenant_id</c>.</summary>
public interface ISiigoInvoiceRepository
{
    /// <summary>Todas las filas del pedido (de los dos tipos y de todos los estados), de la más vieja a la más nueva.</summary>
    Task<IReadOnlyList<SiigoInvoice>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Las filas del tenant con esos ids, en cualquier estado; las de otro tenant no salen. Sin ids, ninguna.</summary>
    Task<IReadOnlyList<SiigoInvoice>> ListByIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken);

    /// <summary>Todas las facturas de flete de esos pedidos del tenant, en cualquier estado. Sin pedidos, ninguna.</summary>
    Task<IReadOnlyList<SiigoInvoice>> ListFreightAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    void Add(SiigoInvoice invoice);
}

/// <summary>Las llamadas a Siigo que fallaron (<c>siigo.call_failures</c>), sólo lectura y sin tracking: la escritura es de
/// <see cref="ISiigoCallFailureLog"/>. Todo filtra por <c>tenant_id</c>.</summary>
public interface ISiigoCallFailureRepository
{
    /// <summary>La página pedida, de la más nueva a la más vieja, y el total con los mismos filtros. Un filtro nulo no se aplica.</summary>
    Task<(IReadOnlyList<SiigoCallFailure> Items, int Total)> SearchAsync(
        Guid tenantId,
        SiigoCallOperation? operation,
        int? status,
        DateTimeOffset? since,
        DateTimeOffset? until,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SiigoCallFailure?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken);
}

/// <summary>Una variante de QEP tal como la tabla de vínculos la pinta: <paramref name="DisplayName"/> es el producto más los valores de
/// sus opciones; <paramref name="IsActive"/> exige variante y producto activos.</summary>
public sealed record SiigoVariantRef(Guid VariantId, string DisplayName, string Code, bool IsActive);

/// <summary>Catalog. Las variantes del tenant para el paso «Productos» (spec 2026-11-18 §5.3). El adaptador lee el catálogo directo
/// (proyección sobre <c>catalog.product_variants</c>, sin cargar el agregado: un producto llega a 2.048 variantes).</summary>
public interface ISiigoVariantCatalog
{
    /// <summary>
    /// Una página de variantes (activas o no), por producto y código. <paramref name="search"/> busca sin distinguir mayúsculas en el
    /// código de la variante y el nombre del producto. <paramref name="onlyVariantIds"/> deja sólo esas (vacía no devuelve filas);
    /// <paramref name="excludeVariantIds"/> las quita: así «vinculadas» y «sin vincular» salen sin cruzar dos esquemas. El total es el de
    /// la búsqueda, no el de la página.
    /// </summary>
    Task<SiigoPage<SiigoVariantRef>> SearchAsync(
        Guid tenantId,
        string? search,
        IReadOnlyCollection<Guid>? onlyVariantIds,
        IReadOnlyCollection<Guid>? excludeVariantIds,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>Las variantes activas del tenant (variante y producto activos), para la cobertura. Con <paramref name="onlyVariantIds"/>,
    /// cuántas de ésas lo son.</summary>
    Task<int> CountActiveAsync(Guid tenantId, IReadOnlyCollection<Guid>? onlyVariantIds, CancellationToken cancellationToken);

    /// <summary>Todas las variantes activas con su código, para «Vincular por código». El <c>DisplayName</c> es el del producto: ahí no
    /// se pinta, y armar el de cada variante costaría las opciones de todo el catálogo.</summary>
    Task<IReadOnlyList<SiigoVariantRef>> ListAllActiveCodesAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>El número de un pedido y el nombre de su cliente, como los muestra <c>/orders</c>.</summary>
public sealed record SiigoOrderLabel(string OrderNumber, string? CustomerName);

/// <summary>Geography. Anota en la bandeja del operador una ciudad del exterior que no se pudo vincular (spec 2026-11-26). Escribe y
/// puede fallar: quien llama no deja que eso tumbe la facturación. Lo llaman sólo la vista previa y la emisión; la disponibilidad no lo
/// recibe.</summary>
public interface ISiigoForeignCityInbox
{
    Task ReportAsync(Guid tenantId, Guid orderId, Invoicing.SiigoUnlinkedCity city, CancellationToken cancellationToken);
}

/// <summary>
/// Quotations. El pedido tal como el armador de la factura lo necesita, y la marca de «facturado» una vez que Siigo la creó (spec
/// 2026-11-18 §5.5). El adaptador (<c>SiigoOrderSource</c>, Bootstrapper) llena <see cref="Invoicing.SiigoInvoiceInput"/> desde el
/// contrato <c>IExternalOrderInvoicing</c> de Quotations y traduce los enums de documento y de persona por nombre; Siigo nunca referencia
/// tipos de Quotations.
/// </summary>
public interface ISiigoOrderSource
{
    /// <summary>La foto del pedido, o <c>null</c> si no es del tenant. Lanza los mismos 422 que la marca manual cuando el pedido no se
    /// puede facturar (anulado, ya facturado, sin pagar, sin datos del cliente).</summary>
    Task<Invoicing.SiigoInvoiceInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>La versión actual del pedido, o <c>null</c> si no es del tenant. Sin las precondiciones de <see cref="GetAsync"/>: se usa
    /// después de crear la factura, cuando el pedido puede haber cambiado, para marcar la fila con <c>order_changed</c>.</summary>
    Task<long?> GetVersionAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Marca el pedido como facturado en Siigo con el número de la factura y su id. Idempotente.</summary>
    Task MarkInvoicedAsync(
        Guid tenantId, Guid orderId, string number, string siigoInvoiceId, Guid memberId, CancellationToken cancellationToken);

    /// <summary>Si el pedido ya está marcado como facturado en Siigo con ese número. Sólo lee: la tanda lo pregunta al recuperar un envío
    /// que no dejó resultado.</summary>
    Task<bool> IsMarkedInvoicedAsync(Guid tenantId, Guid orderId, string number, CancellationToken cancellationToken);

    /// <summary>Quita la marca de facturado en Siigo con ese número, porque la factura se anuló allá (spec 2026-11-25 §4). Con otra marca,
    /// o sin ninguna, no hace nada.</summary>
    /// <returns>Si el pedido perdió la marca.</returns>
    Task<bool> UnmarkInvoicedAsync(Guid tenantId, Guid orderId, string number, Guid memberId, CancellationToken cancellationToken);

    /// <summary>El número y el cliente de estos pedidos, en lote, para pintar una tanda (spec 2026-11-24 §5). Un id que no es del tenant
    /// no aparece. No se guardan en Siigo: un pedido puede cambiar de cliente y la tanda mostraría el viejo.</summary>
    Task<IReadOnlyDictionary<Guid, SiigoOrderLabel>> ListLabelsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);
}

/// <summary>
/// Quotations. Entregar el PDF de una factura de Siigo al WhatsApp del cliente del pedido (spec 2026-11-21). El adaptador
/// (<c>SiigoInvoiceWhatsApp</c>, Bootstrapper) sólo reenvía a <c>IInvoiceWhatsAppDelivery</c>: canal, teléfono, plantilla y copia pública son
/// de Quotations y Siigo no los conoce. Los errores de dominio (<c>invoice.whatsapp.*</c>) salen tal cual.
/// </summary>
public interface ISiigoInvoiceWhatsApp
{
    /// <summary>Manda la factura. El PDF viaja en memoria: ni Siigo ni este puerto lo guardan.</summary>
    Task SendAsync(
        Guid tenantId, Guid orderId, string invoiceNumber, decimal? invoiceTotal, ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken);

    /// <summary>Si hoy se podría mandar (canal que se resuelve y cliente con teléfono). Que falte algo (sin canal, sin teléfono) responde <c>false</c>; una falla de infraestructura sí lanza, y quien la llama (la lista) decide qué hacer con ella.</summary>
    Task<bool> CanSendAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}
