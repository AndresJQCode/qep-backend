namespace Modules.Tenancy.Domain;

/// <summary>
/// Una clave de módulo comercial (spec 2026-10-07, «Catálogo de módulos»). Sus únicas instancias son
/// las de <see cref="TenantModuleKeys"/>: el constructor es privado, así que una clave inventada no
/// compila. El texto sólo existe en la base y en el JSON.
/// </summary>
public sealed record TenantModuleKey
{
    private TenantModuleKey(string value) => Value = value;

    public string Value { get; }

    internal static TenantModuleKey Define(string value) => new(value);

    /// <summary>
    /// La única conversión de texto a clave. La usa el mapeo de EF: la base es el único lugar donde
    /// una clave llega como texto. Lanza con cualquier otro texto —ignorarlo escondería una base
    /// corrupta—, y no ignora mayúsculas, igual que el <c>CHECK</c>. No hay <c>TryParse</c>: nadie
    /// necesita ignorar una clave desconocida.
    /// </summary>
    public static TenantModuleKey Parse(string value) =>
        TenantModuleKeys.All.FirstOrDefault(key => string.Equals(key.Value, value, StringComparison.Ordinal))
        ?? throw new ArgumentException($"'{value}' is not a known tenant module key.", nameof(value));

    public override string ToString() => Value;
}

/// <summary>
/// La lista cerrada de módulos. <see cref="All"/> está en orden topológico —cada clave después de
/// todas sus dependencias—, que es el orden de la respuesta de <c>/modules</c>, el del <c>CHECK</c> y
/// del que depende <see cref="TenantModuleSet.FromStored"/>. Un módulo nuevo es otra migración que
/// cambia el <c>CHECK</c>, a propósito.
/// </summary>
public static class TenantModuleKeys
{
    public static readonly TenantModuleKey Catalog = TenantModuleKey.Define("catalog");
    public static readonly TenantModuleKey Customers = TenantModuleKey.Define("customers");
    public static readonly TenantModuleKey Companies = TenantModuleKey.Define("companies");
    public static readonly TenantModuleKey Quotations = TenantModuleKey.Define("quotations");
    public static readonly TenantModuleKey Orders = TenantModuleKey.Define("orders");
    public static readonly TenantModuleKey Reporting = TenantModuleKey.Define("reporting");
    public static readonly TenantModuleKey Pos = TenantModuleKey.Define("pos");

    /// <summary>Spec 2026-10-09, decisión 4: la bandeja de WhatsApp. Último de la lista, sin
    /// dependencias y fuera de <see cref="DefaultForNewTenants"/>.</summary>
    public static readonly TenantModuleKey Messaging = TenantModuleKey.Define("messaging");

    /// <summary>Spec 2026-10-13, D3 y D30: el embudo de ventas. Último de la lista, depende sólo de
    /// <see cref="Customers"/> y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Pipeline = TenantModuleKey.Define("pipeline");

    /// <summary>Spec 2026-10-18 (Shopify), D-S20: pedidos de la tienda Shopify. Último de la lista, depende sólo de
    /// <see cref="Orders"/> y entra en <see cref="DefaultForNewTenants"/>; sin backfill de tenants existentes.</summary>
    public static readonly TenantModuleKey Shopify = TenantModuleKey.Define("shopify");

    /// <summary>Spec 2026-10-21, D-C11: el constructor de chatbots. Depende sólo de
    /// <see cref="Messaging"/> y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Automations = TenantModuleKey.Define("automations");

    /// <summary>Spec 2026-10-23, D-12: directorio de proveedores. Último de la lista, sin dependencias y fuera de
    /// <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Suppliers = TenantModuleKey.Define("suppliers");

    /// <summary>Spec 2026-10-29: contratos de servicio (cobros recurrentes). Depende de <see cref="Customers"/> (cobra a un
    /// cliente) y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant. La clave sigue siendo <c>billing</c>
    /// aunque el módulo se llame <c>ServiceContracts</c> desde 2026-10-30: está en <c>tenant_modules</c>, en los
    /// <c>RequiredModules</c> de sus permisos y en el frontend, y cambiarla sería una migración de datos.</summary>
    public static readonly TenantModuleKey ServiceContracts = TenantModuleKey.Define("billing");

    /// <summary>Spec 2026-10-30, D7: las claves de API de integración. Último de la lista, sin dependencias y fuera de
    /// <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Developer = TenantModuleKey.Define("developer");

    public static readonly IReadOnlyList<TenantModuleKey> All =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos, Messaging, Pipeline, Shopify, Automations, Suppliers, ServiceContracts, Developer];

    /// <summary>Los seis base más <c>shopify</c>, sin <c>pos</c>, <c>messaging</c> ni <c>pipeline</c>: el signup con el interruptor prendido.</summary>
    public static readonly IReadOnlyList<TenantModuleKey> DefaultForNewTenants =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Shopify];

    // POS no depende de customers, ni opcionalmente: el spec de POS quitó la selección de cliente.
    private static readonly Dictionary<TenantModuleKey, TenantModuleKey[]> Dependencies = new()
    {
        [Quotations] = [Catalog, Customers, Companies],
        [Orders] = [Quotations],
        [Pos] = [Catalog, Companies],
        // Spec 2026-10-13 D30: sin messaging el triage queda vacío; sin quotations no llegan eventos.
        [Pipeline] = [Customers],
        [Shopify] = [Orders],
        // Spec 2026-10-21 D-C11: un chatbot responde por la bandeja; sin messaging no hay conversaciones.
        [Automations] = [Messaging],
        // Spec 2026-10-29: un cobro recurrente se emite a un cliente.
        [ServiceContracts] = [Customers],
    };

    public static IReadOnlyList<TenantModuleKey> DependenciesOf(TenantModuleKey key) =>
        Dependencies.TryGetValue(key, out var dependencies) ? dependencies : [];
}
