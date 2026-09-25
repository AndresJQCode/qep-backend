namespace Modules.Quotations.Domain;

/// <summary>
/// Cómo un tenant homologa el Excel de pedidos a su ERP (spec 2026-09-24): qué columnas viajan,
/// con qué encabezado y en qué orden, más las fijas de texto que su ERP exige (D2, D4). Uno por
/// tenant, agregado propio de Quotations y no una columna de <c>Tenant</c> (D6): el processor lo
/// lee de su propio repositorio y tiene versión propia, así que guardar columnas con la pantalla
/// del logo abierta no da un 412 cruzado.
///
/// La lista se guarda y viaja entera (D7): la posición es el índice. Lo guardado no tiene por qué
/// traer las 33 llaves: <see cref="Effective(OrdersExportLayout?)"/> completa con el catálogo (D8).
/// </summary>
public sealed class OrdersExportLayout
{
    public const int MaxFixedColumns = 10;

    /// <summary>La versión que responde el layout no guardado (D9): el primer PUT viaja con
    /// <c>If-Match: "1"</c> y la fila nace en 2.</summary>
    public const long DefaultVersion = 1;

    private readonly List<OrdersExportColumnSetting> _columns = [];

    private OrdersExportLayout()
    {
    }

    private OrdersExportLayout(
        Guid tenantId, IReadOnlyList<OrdersExportColumnSetting> columns, DateTimeOffset now)
    {
        TenantId = tenantId;
        _columns.AddRange(columns);
        Version = DefaultVersion;
        UpdatedAt = now;
    }

    public Guid TenantId { get; private set; }

    /// <summary>En orden: la posición es el índice.</summary>
    public IReadOnlyList<OrdersExportColumnSetting> Columns => _columns;

    public long Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// El layout que todo tenant tiene sin haber guardado nada: el catálogo tal cual, en
    /// <see cref="DefaultVersion"/>. Existe en memoria para que el primer PUT pase por el mismo
    /// <see cref="Replace"/> y el mismo chequeo de versión que los demás; si no cambia nada, no
    /// hay fila que crear.
    /// </summary>
    public static OrdersExportLayout CreateDefault(Guid tenantId, DateTimeOffset now) =>
        new(tenantId, Effective(stored: null), now);

    /// <summary>D8 sobre el catálogo real: lo que ven GET y el processor.</summary>
    public static IReadOnlyList<OrdersExportColumnSetting> Effective(OrdersExportLayout? stored) =>
        Effective(stored?.Columns ?? [], OrdersExportColumnCatalog.Columns);

    /// <summary>
    /// D8, como función pura: las entradas guardadas en su orden —una llave que ya no está en el
    /// catálogo se descarta en silencio—, más toda llave del catálogo que no esté guardada, al
    /// final, visible y con su nombre por defecto. Así una columna nueva del backend aparece sola
    /// sin obligar al tenant a re-guardar, un PUT no exige las 33, y sin fila guardada el efectivo
    /// es el catálogo tal cual.
    /// </summary>
    public static IReadOnlyList<OrdersExportColumnSetting> Effective(
        IReadOnlyList<OrdersExportColumnSetting> stored,
        IReadOnlyList<OrdersExportCatalogColumn> catalog)
    {
        var known = catalog.Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var effective = new List<OrdersExportColumnSetting>(stored.Count + catalog.Count);

        foreach (var column in stored)
        {
            if (column.Kind == OrdersExportColumnKind.Fixed)
            {
                effective.Add(column);
                continue;
            }

            if (column.Key is { } key && known.Contains(key) && seen.Add(key))
            {
                effective.Add(column);
            }
        }

        foreach (var column in catalog)
        {
            if (seen.Add(column.Key))
            {
                effective.Add(OrdersExportColumnSetting.Catalog(column.Key, column.DefaultHeader, visible: true));
            }
        }

        return effective;
    }
}
