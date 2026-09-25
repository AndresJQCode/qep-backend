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

    /// <summary>
    /// Reemplaza la lista entera (D7). La entrada se completa con el catálogo antes de validar
    /// (D8): las reglas de la lista entera se miden sobre lo que el Excel va a mostrar, y lo que
    /// se guarda es esa lista completa. Todo se valida antes de tocar un solo campo.
    /// </summary>
    /// <returns>
    /// <c>false</c> —sin tocar columnas, versión ni fecha— cuando la lista completada es la que ya
    /// está guardada, entrada por entrada. Mismo criterio que <c>Membership.UpdateProfile</c>:
    /// guardar sin tocar no consume versión, que daría un 412 falso en otra pantalla abierta.
    /// </returns>
    public bool Replace(IReadOnlyList<OrdersExportColumnSetting> columns, DateTimeOffset now)
    {
        // Antes de completar: una llave que no es del catálogo es un error del cuerpo, no algo que
        // Effective descarte en silencio (eso es para lo ya guardado cuando el catálogo cambia).
        EnsureKnownKeys(columns);
        var completed = Effective(columns, OrdersExportColumnCatalog.Columns);
        Validate(completed);

        if (IsSameAs(completed))
        {
            return false;
        }

        _columns.Clear();
        _columns.AddRange(completed);
        Version++;
        UpdatedAt = now;
        return true;
    }

    private bool IsSameAs(IReadOnlyList<OrdersExportColumnSetting> columns) =>
        columns.Count == _columns.Count
        && columns.Zip(_columns).All(pair => pair.First.IsSameAs(pair.Second));

    // Llave desconocida o repetida → columns_invalid. Ordinal: "Company" no es "company"
    // (Review Focus 5). Las fijas no tienen llave y no cuentan acá.
    private static void EnsureKnownKeys(IReadOnlyList<OrdersExportColumnSetting> columns)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column.Kind == OrdersExportColumnKind.Fixed)
            {
                continue;
            }

            if (column.Key is not { } key || !OrdersExportColumnCatalog.Contains(key) || !keys.Add(key))
            {
                throw new QuotationsDomainException(
                    "quotations.orders_export_layout.columns_invalid",
                    "Every catalog column must use a known key, at most once.");
            }
        }
    }

    // Sobre la lista completada. En este orden, y la pantalla lo sabe: tope de fijas, alguna
    // visible, duplicado entre visibles.
    private static void Validate(IReadOnlyList<OrdersExportColumnSetting> columns)
    {
        if (columns.Count(column => column.Kind == OrdersExportColumnKind.Fixed) > MaxFixedColumns)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.too_many_fixed_columns",
                $"A layout can have at most {MaxFixedColumns} fixed columns.");
        }

        var visible = columns.Where(column => column.Visible).ToArray();
        if (visible.Length == 0)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.all_hidden",
                "At least one column must be visible.");
        }

        // El ERP lee por encabezado: dos visibles iguales se pisan. Entre ocultas puede repetirse.
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in visible)
        {
            if (!headers.Add(column.Header))
            {
                throw new QuotationsDomainException(
                    "quotations.orders_export_layout.header_duplicated",
                    $"Two visible columns share the header '{column.Header}'.");
            }
        }
    }

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
