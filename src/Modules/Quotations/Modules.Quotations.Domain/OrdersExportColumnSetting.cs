namespace Modules.Quotations.Domain;

/// <summary>Qué es cada entrada del layout (spec 2026-09-24, D3 y D4): una columna del catálogo,
/// identificada por su llave, o una fija del tenant, identificada por su posición.</summary>
public enum OrdersExportColumnKind
{
    Catalog,
    Fixed,
}

/// <summary>
/// Una entrada del layout de columnas del Excel de pedidos (spec 2026-09-24). Se construye por
/// <see cref="Catalog"/> o <see cref="Fixed"/>, y ahí se normaliza y se valida lo que no necesita
/// mirar el resto de la lista —encabezado y valor—: un rechazo corta antes de que
/// <see cref="OrdersExportLayout.Replace"/> toque nada. Lo que depende de la lista entera
/// (llaves, duplicados, visibles, tope de fijas) lo revisa el agregado.
///
/// Inmutable después de construida: cambiar una columna es reemplazar la lista entera (D7). En la
/// base es una entrada del <c>jsonb</c> de <c>orders_export_layouts.columns</c>, con <c>kind</c>
/// como texto.
/// </summary>
public sealed class OrdersExportColumnSetting
{
    public const int HeaderMaxLength = 64;

    public const int FixedValueMaxLength = 128;

    // EF materializa la entrada desde el JSON con este constructor y los setters privados.
    private OrdersExportColumnSetting()
    {
        Header = string.Empty;
    }

    private OrdersExportColumnSetting(
        OrdersExportColumnKind kind, string? key, string header, string? value, bool visible)
    {
        Kind = kind;
        Key = key;
        Header = header;
        Value = value;
        Visible = visible;
    }

    public OrdersExportColumnKind Kind { get; private set; }

    /// <summary>La llave del catálogo, tal como llegó: ni recortada ni normalizada, porque es un
    /// identificador y no un texto. Nula en una fija.</summary>
    public string? Key { get; private set; }

    /// <summary>El encabezado que el ERP del tenant lee. Recortado en los extremos —los espacios
    /// internos son parte del nombre—, nunca vacío, hasta <see cref="HeaderMaxLength"/>.</summary>
    public string Header { get; private set; }

    /// <summary>El texto que una fija repite en cada fila (D4). Nulo en una columna del catálogo;
    /// en una fija nunca nulo, y vacío es válido: un ERP puede exigir la columna aunque venga en
    /// blanco.</summary>
    public string? Value { get; private set; }

    public bool Visible { get; private set; }

    public static OrdersExportColumnSetting Catalog(string key, string? header, bool visible) =>
        new(OrdersExportColumnKind.Catalog, key, NormalizeHeader(header), value: null, visible);

    public static OrdersExportColumnSetting Fixed(string? header, string? value, bool visible) =>
        new(OrdersExportColumnKind.Fixed, key: null, NormalizeHeader(header), NormalizeFixedValue(value), visible);

    /// <summary>La misma entrada, campo por campo y ordinal: es lo que decide el no-op de
    /// <see cref="OrdersExportLayout.Replace"/>. Ordinal a propósito —<c>Empresa</c> y
    /// <c>EMPRESA</c> son un cambio— aunque el duplicado de visibles no distinga mayúsculas.</summary>
    public bool IsSameAs(OrdersExportColumnSetting other) =>
        Kind == other.Kind
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && string.Equals(Header, other.Header, StringComparison.Ordinal)
        && string.Equals(Value, other.Value, StringComparison.Ordinal)
        && Visible == other.Visible;

    // Review Focus 1: se recortan los extremos y nada más. Colapsar espacios internos cambiaría lo
    // que el tenant escribió, y el ERP compara texto.
    private static string NormalizeHeader(string? header)
    {
        var trimmed = header?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > HeaderMaxLength)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.header_invalid",
                $"A column header is required and cannot exceed {HeaderMaxLength} characters.");
        }

        return trimmed;
    }

    // Vacío es válido (D4); sólo espacios se guarda vacío (Review Focus 3).
    private static string NormalizeFixedValue(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length > FixedValueMaxLength)
        {
            throw new QuotationsDomainException(
                "quotations.orders_export_layout.fixed_value_invalid",
                $"A fixed column value cannot exceed {FixedValueMaxLength} characters.");
        }

        return trimmed;
    }
}
