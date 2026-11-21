using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.Application;

// Spec 2026-11-21 §5.1: los puertos de Coordinadora hacia los demás módulos. Coordinadora sólo ve Tenancy y Audit; la conexión
// (Integrations), el pedido y su destinatario (Quotations, Customers) y la ciudad (Geography) entran por estos puertos y su adaptador
// vive en src/Bootstrapper/Coordinadora*.cs (CoordinadoraLayerTests).

/// <summary>Una conexión de Coordinadora del tenant, con su estado en Integrations (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>).</summary>
public sealed record CoordinadoraConnectionInfo(Guid Id, string Name, string Status);

/// <summary>
/// El resultado de resolver la conexión: sus credenciales ya descifradas, o por qué no se puede usar (<c>not_found</c> también para la de
/// otro proveedor, <c>paused</c>, <c>needs_attention</c>, <c>provider_hidden</c>, <c>provider_disabled</c>, <c>secret_unreadable</c>,
/// <c>incomplete</c>). <see cref="Credentials"/> no es <c>null</c> sólo si <see cref="UnavailableReason"/> lo es.
/// </summary>
public sealed record CoordinadoraConnectionResolution(CoordinadoraCredentials? Credentials, string? UnavailableReason);

/// <summary>Las conexiones de Coordinadora de Integrations. El adaptador usa <c>IIntegrationConnections</c> (como
/// <c>SiigoConnectionDirectory</c>); Coordinadora no referencia Integrations.</summary>
public interface ICoordinadoraConnectionDirectory
{
    /// <summary>La conexión de Coordinadora <b>del tenant</b>, en cualquier estado; <c>null</c> si no existe, es de otro tenant o de otro
    /// proveedor (el llamador responde 403, nunca 404).</summary>
    Task<CoordinadoraConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Las credenciales para <b>operar</b> (cotizar, generar, rastrear, anular): sólo una conexión <c>Active</c>.</summary>
    Task<CoordinadoraConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>Todas las del tenant (el proveedor admite una), por nombre.</summary>
    Task<IReadOnlyList<CoordinadoraConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Una ciudad de Geography como Coordinadora la pide: el código de 8 dígitos es DIVIPOLA + <c>000</c> (README § Nombres de ciudad
/// de Coordinadora) y <see cref="CoordinadoraName"/> es <c>null</c> cuando Coordinadora no la lista o la lista inactiva.</summary>
public sealed record CoordinadoraCityRef(Guid CityId, string Name, string DivipolaCode, string? CoordinadoraName)
{
    public string CoordinadoraCode => DivipolaCode + "000";

    public bool IsServed => !string.IsNullOrWhiteSpace(CoordinadoraName);
}

/// <summary>Geography. El adaptador lee <c>City.DivipolaCode</c> y <c>City.CoordinadoraName</c>; una ciudad que no existe es <c>null</c>.</summary>
public interface ICoordinadoraGeographyLookup
{
    Task<CoordinadoraCityRef?> FindCityAsync(Guid cityId, CancellationToken cancellationToken);
}

/// <summary>Una línea física del pedido, para el <c>contenido</c> de la guía.</summary>
public sealed record CoordinadoraShipmentLine(string Name, string? Code, decimal Quantity);

/// <summary>
/// A quién se le entrega, ya resuelto por Quotations: la parte de envío del pedido o, sin ella, la ficha del cliente (spec §5.3).
/// <paramref name="Country"/> <c>null</c> es Colombia; fuera de Colombia la ciudad viene como texto en <paramref name="CityName"/> y no
/// hay <paramref name="CityId"/>. <paramref name="Identification"/> es la de la parte de facturación o del cliente, si la hay. Lleva
/// datos del destinatario: el <c>ToString</c> no los escribe.
/// </summary>
public sealed record CoordinadoraRecipientInput(
    string? Name,
    string? Phone,
    string? Address,
    Guid? CityId,
    string? Country,
    string? CityName,
    string? Identification)
{
    public override string ToString() => "CoordinadoraRecipientInput { }";
}

/// <summary>
/// La foto del pedido que el armador de la guía necesita (spec §5.7). <paramref name="ProductsTotal"/> es la suma de las líneas con IVA
/// (el valor declarado); <paramref name="NetTotal"/> lo que se recauda contraentrega. <paramref name="CarrierName"/> es la foto de la
/// transportadora elegida en el pedido, o <c>null</c>. <paramref name="Today"/> es hoy en la zona del tenant.
/// </summary>
public sealed record CoordinadoraShipmentInput(
    Guid OrderId,
    string OrderNumber,
    DateOnly Today,
    bool IsCancelled,
    bool IsStorePickup,
    bool RequiresShipping,
    string? CarrierName,
    bool CashOnDelivery,
    decimal ProductsTotal,
    decimal NetTotal,
    string? Notes,
    long Version,
    IReadOnlyList<CoordinadoraShipmentLine> Lines,
    CoordinadoraRecipientInput Recipient)
{
    public override string ToString() => $"CoordinadoraShipmentInput {{ OrderId = {OrderId}, OrderNumber = {OrderNumber} }}";
}

/// <summary>Quotations. El pedido tal como el armador lo necesita; el adaptador (<c>CoordinadoraOrderSource</c>, Bootstrapper) lo llena
/// desde el contrato <c>IExternalOrderShipping</c> de Quotations. Coordinadora nunca referencia tipos de Quotations.</summary>
public interface ICoordinadoraOrderSource
{
    /// <summary>La foto del pedido, o <c>null</c> si no es del tenant. Sin precondiciones: las decide el armador, que las junta todas.</summary>
    Task<CoordinadoraShipmentInput?> GetAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}

/// <summary>El repositorio de la configuración (<c>coordinadora.settings</c>). Con tracking: el llamador modifica y guarda con
/// <see cref="ICoordinadoraUnitOfWork"/>. Todo filtra por <c>tenant_id</c>.</summary>
public interface ICoordinadoraSettingsRepository
{
    /// <summary>La configuración de la conexión; si todavía no existe, la crea vacía con <c>INSERT … ON CONFLICT DO NOTHING</c> (dos
    /// primeros accesos a la vez no chocan). <c>null</c> sólo si la fila existe con otro tenant.</summary>
    Task<CoordinadoraSettings?> FindOrCreateAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

    /// <summary>La configuración de la conexión si ya existe, o <c>null</c>. Para la guía: generar nunca crea la configuración.</summary>
    Task<CoordinadoraSettings?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Las guías (<c>coordinadora.shipments</c>). Con tracking. Todo filtra por <c>tenant_id</c>.</summary>
public interface ICoordinadoraShipmentRepository
{
    /// <summary>Todas las filas del pedido, de la más vieja a la más nueva.</summary>
    Task<IReadOnlyList<CoordinadoraShipment>> ListByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    void Add(CoordinadoraShipment shipment);
}
