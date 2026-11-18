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

    Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken);

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
}
