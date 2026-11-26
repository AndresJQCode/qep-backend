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

    /// <summary>Spec 2026-10-30, D7: las claves de API de integración. Sin dependencias y fuera de
    /// <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Developer = TenantModuleKey.Define("developer");

    /// <summary>Spec 2026-10-31: préstamos. Depende de <see cref="Customers"/> (el préstamo se otorga a un cliente) y queda
    /// fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Loans = TenantModuleKey.Define("loans");

    /// <summary>Spec 2026-11-03 §4.2: tienda pública. Depende de <see cref="Orders"/> (el checkout de la tienda crea el pedido directo)
    /// y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Store = TenantModuleKey.Define("store");

    /// <summary>Spec 2026-10-29 (agentes IA), D-1b: agentes que atienden la bandeja con el modelo del proveedor que elige el
    /// tenant. Depende sólo de <see cref="Messaging"/>, no de <see cref="Automations"/>: un agente no es un chatbot. Queda
    /// fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Ai = TenantModuleKey.Define("ai");

    /// <summary>Spec 2026-11-17 (copiloto interno), D-C2: el chat privado del equipo con los datos de su organización. Sin
    /// dependencias: no cuelga de <see cref="Ai"/>, que exige <see cref="Messaging"/>, porque un tenant sin bandeja también
    /// pregunta por sus ventas. Va justo después de <see cref="Ai"/> (adenda D-14, mismo orden que el frontend) y queda fuera
    /// de <see cref="DefaultForNewTenants"/> (OC-4): se prende por tenant.</summary>
    public static readonly TenantModuleKey Copilot = TenantModuleKey.Define("copilot");

    /// <summary>Spec 2026-11-14: soporte (tickets y PQRSF). Sin dependencias (C9): un ticket puede no tener cliente y el
    /// calendario se lee aunque <see cref="Messaging"/> esté apagado. Queda fuera de <see cref="DefaultForNewTenants"/>: se
    /// prende por tenant.</summary>
    public static readonly TenantModuleKey Support = TenantModuleKey.Define("support");

    /// <summary>Spec 2026-11-17 (compras): órdenes de compra a proveedores. Depende de <see cref="Suppliers"/> y
    /// <see cref="Catalog"/> (la línea es una variante del catálogo, el proveedor sale del directorio) y queda fuera de
    /// <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Purchases = TenantModuleKey.Define("purchases");

    /// <summary>Spec 2026-11-17 (mayorista): listas de precios, distribuidores e invitaciones. Depende de <see cref="Orders"/>
    /// (el pedido del distribuidor es un <c>Order</c>) y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por
    /// tenant.</summary>
    public static readonly TenantModuleKey Wholesale = TenantModuleKey.Define("wholesale");

    /// <summary>Spec 2026-11-18 (integración Siigo): factura electrónica de un pedido en Siigo. Depende de <see cref="Orders"/>
    /// (lo que se factura es un <c>Order</c>) y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Siigo = TenantModuleKey.Define("siigo");

    /// <summary>Spec 2026-11-20 (RRHH fase 1). Depende de <see cref="Companies"/> (la empresa empleadora) y queda fuera de
    /// <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Hr = TenantModuleKey.Define("hr");

    /// <summary>Spec 2026-11-21 (nómina, proyecto 1). Depende de <see cref="Hr"/> (empleados y contratos), que depende de
    /// <see cref="Companies"/>; queda fuera de <see cref="DefaultForNewTenants"/>: se prende y se vende por tenant.</summary>
    public static readonly TenantModuleKey Payroll = TenantModuleKey.Define("payroll");

    /// <summary>Spec 2026-11-21 (Agenda): citas, franjas, recursos y cupos. Depende de <see cref="Customers"/> (toda cita es de un
    /// cliente) y queda fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant. Mensajería es opcional: sin ella la Agenda
    /// funciona y sólo el recordatorio por WhatsApp no se ofrece.</summary>
    public static readonly TenantModuleKey Scheduling = TenantModuleKey.Define("scheduling");

    /// <summary>Spec WMS 2026-10-31 §1.1: inventario de las variantes del catálogo. Depende de <see cref="Catalog"/> y queda
    /// fuera de <see cref="DefaultForNewTenants"/>: se prende por tenant.</summary>
    public static readonly TenantModuleKey Wms = TenantModuleKey.Define("wms");

    public static readonly IReadOnlyList<TenantModuleKey> All =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos, Messaging, Pipeline, Shopify, Automations, Suppliers, ServiceContracts, Developer, Loans, Store, Ai, Copilot, Support, Purchases, Wholesale, Siigo, Hr, Payroll, Scheduling, Wms];

    /// <summary>Los seis base más <c>shopify</c>, sin <c>pos</c>, <c>messaging</c> ni <c>pipeline</c>: el signup con el interruptor prendido.</summary>
    public static readonly IReadOnlyList<TenantModuleKey> DefaultForNewTenants =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Shopify];

    // POS no depende de customers, ni opcionalmente: el spec de POS quitó la selección de cliente.
    private static readonly Dictionary<TenantModuleKey, TenantModuleKey[]> Dependencies = new()
    {
        [Quotations] = [Catalog, Customers, Companies],
        // Un pedido nace sin cotización (spec 2026-10-15) y se vende sin ella: depende de lo que la
        // cotización ya exigía, no de ella, para que la consola de plataforma pueda ocultar cotizaciones.
        [Orders] = [Catalog, Customers, Companies],
        [Pos] = [Catalog, Companies],
        // Spec 2026-10-13 D30: sin messaging el triage queda vacío; sin quotations no llegan eventos.
        [Pipeline] = [Customers],
        [Shopify] = [Orders],
        // Spec 2026-10-21 D-C11: un chatbot responde por la bandeja; sin messaging no hay conversaciones.
        [Automations] = [Messaging],
        // Spec 2026-10-29: un cobro recurrente se emite a un cliente.
        [ServiceContracts] = [Customers],
        // Spec 2026-10-31: un préstamo se otorga a un cliente.
        [Loans] = [Customers],
        // Spec 2026-11-03: el checkout de la tienda crea el pedido directo.
        [Store] = [Orders],
        // Spec 2026-10-29 D-1b: un agente responde por la bandeja; sin messaging no hay conversaciones.
        [Ai] = [Messaging],
        // Spec 2026-11-17 (compras): la orden se emite a un proveedor del directorio y cada línea es una variante del catálogo.
        [Purchases] = [Suppliers, Catalog],
        // Spec 2026-11-17 (mayorista): el pedido del distribuidor es un Order del mayorista.
        [Wholesale] = [Orders],
        // Spec 2026-11-18 (integración Siigo): lo que se factura es un pedido.
        [Siigo] = [Orders],
        // Spec 2026-11-20 (RRHH fase 1): la empresa empleadora es una empresa de Companies.
        [Hr] = [Companies],
        // Spec 2026-11-21 (nómina, proyecto 1): liquida a los empleados de Hr.
        [Payroll] = [Hr],
        // Spec 2026-11-21 (Agenda): toda cita es de un cliente.
        [Scheduling] = [Customers],
        // Spec WMS 2026-10-31 §1.1: el inventario es de variantes del catálogo.
        [Wms] = [Catalog],
    };

    public static IReadOnlyList<TenantModuleKey> DependenciesOf(TenantModuleKey key) =>
        Dependencies.TryGetValue(key, out var dependencies) ? dependencies : [];
}
