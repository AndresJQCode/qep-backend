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

    public static readonly IReadOnlyList<TenantModuleKey> All =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting, Pos];

    /// <summary>Los seis de hoy, sin <c>pos</c>: el backfill y el signup con el interruptor prendido.</summary>
    public static readonly IReadOnlyList<TenantModuleKey> DefaultForNewTenants =
        [Catalog, Customers, Companies, Quotations, Orders, Reporting];

    // POS no depende de customers, ni opcionalmente: el spec de POS quitó la selección de cliente.
    private static readonly Dictionary<TenantModuleKey, TenantModuleKey[]> Dependencies = new()
    {
        [Quotations] = [Catalog, Customers, Companies],
        [Orders] = [Quotations],
        [Pos] = [Catalog, Companies],
    };

    public static IReadOnlyList<TenantModuleKey> DependenciesOf(TenantModuleKey key) =>
        Dependencies.TryGetValue(key, out var dependencies) ? dependencies : [];
}
