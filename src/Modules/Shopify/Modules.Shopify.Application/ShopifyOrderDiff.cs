using System.Text.Json.Nodes;
using Modules.Shopify.Domain;

namespace Modules.Shopify.Application;

/// <summary>Los grupos de un cambio de Shopify (spec 2026-11-14 §3). Viajan por nombre.</summary>
public enum ShopifyEditGroupKind
{
    Lines,
    Shipping,
    ShippingAddress,
    Buyer,
    Note,
    Attributes,
}

/// <summary>Una fila del antes y después de un grupo, lista para pintar (BFF): <see cref="Before"/> y <see cref="After"/> son
/// texto, número crudo o booleano (o el objeto de una línea), nunca un enum de QEP ni un monto preformateado (plan D19).
/// <see cref="Label"/> es el rótulo de la fila cuando el campo se repite (la línea, la clave del atributo).</summary>
public sealed record ShopifyEditRow(string Field, string? Label, JsonNode? Before, JsonNode? After);

public sealed record ShopifyGroupDiff(ShopifyEditGroupKind Kind, IReadOnlyList<ShopifyEditRow> Rows);

/// <summary>
/// Spec 2026-11-14 §3: el diff por grupo entre la versión de Shopify aplicada y una nueva. Compara Shopify contra Shopify (enfoque
/// A1): lo que el asesor cambió en QEP no aparece acá; eso lo decide el planificador contra la proyección.
/// <list type="bullet">
/// <item>Líneas: por id de línea de Shopify; si a alguna línea de cualquiera de los dos lados le falta el id (payload anterior al
/// spec), las de los dos lados por su posición. Cuenta la cantidad vigente,
/// el precio, el descuento, el SKU y el título. Una línea quitada (<see cref="ShopifyLine.IsRemoved"/>) o ausente tiene
/// <c>after</c> nulo; una nueva, <c>before</c> nulo.</item>
/// <item>Envío: título de la transportadora, valor, recogida y contraentrega, con las mismas reglas del importador.</item>
/// <item>Dirección: nombre, teléfono, dirección 1 y 2, ciudad, departamento y código postal, recortados; vacío es
/// <c>null</c>. Sin fila de código postal si uno de los dos payloads no lo guardó
/// (<see cref="ShopifyAddress.ZipUnrecorded"/>).</item>
/// <item>Comprador: documento (de la fuente que configuró la tienda), correo y teléfono.</item>
/// <item>Nota y atributos (sólo las claves que cambiaron).</item>
/// </list>
/// No mira el pago, la cancelación ni <c>updated_at</c>: de eso se encarga §6.3.
/// </summary>
public static class ShopifyOrderDiff
{
    /// <summary>Sólo los grupos con al menos una fila, en el orden de <see cref="ShopifyEditGroupKind"/>.</summary>
    public static IReadOnlyList<ShopifyGroupDiff> Compute(ShopifyOrderSnapshot before, ShopifyOrderSnapshot after, ShopifyStore store)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(store);
        var groups = new List<ShopifyGroupDiff>(6);
        Add(groups, ShopifyEditGroupKind.Lines, LineRows(before, after));
        Add(groups, ShopifyEditGroupKind.Shipping, ShippingRows(before, after));
        Add(groups, ShopifyEditGroupKind.ShippingAddress, AddressRows(before.ShippingAddress, after.ShippingAddress));
        Add(groups, ShopifyEditGroupKind.Buyer, BuyerRows(before, after, store));
        Add(groups, ShopifyEditGroupKind.Note, Rows(Row("note", null, Text(before.Note), Text(after.Note))));
        Add(groups, ShopifyEditGroupKind.Attributes, AttributeRows(before.Attributes, after.Attributes));
        return groups;
    }

    /// <summary>15000.00 y 15000 son el mismo valor: el número viaja sin los ceros que el parser conserva del texto de Shopify.</summary>
    public static decimal Normalized(decimal value) => value / 1.000000000000000000000000000m;

    /// <summary>El rótulo de una línea: <c>título (SKU)</c>, o sólo el título sin SKU (una personalizada).</summary>
    public static string LineLabel(ShopifyLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var sku = Blank(line.Sku);
        var title = Blank(line.Title);
        return sku is null ? title ?? string.Empty : title is null ? sku : $"{title} ({sku})";
    }

    private static void Add(List<ShopifyGroupDiff> groups, ShopifyEditGroupKind kind, List<ShopifyEditRow> rows)
    {
        if (rows.Count > 0)
        {
            groups.Add(new ShopifyGroupDiff(kind, rows));
        }
    }

    private static List<ShopifyEditRow> LineRows(ShopifyOrderSnapshot before, ShopifyOrderSnapshot after)
    {
        // Review I-1: si a una línea de cualquiera de los dos lados le falta el id (un payload guardado antes del spec contra uno
        // nuevo), los dos lados van por posición; si no, el primer webhook después del despliegue quitaría y agregaría cada línea.
        var byPosition = before.Lines.Concat(after.Lines).Any(line => line.Id is null);
        var previous = Keyed(before.Lines, byPosition);
        var current = Keyed(after.Lines, byPosition);
        var rows = new List<ShopifyEditRow>();
        foreach (var key in previous.Keys.Concat(current.Keys.Where(key => !previous.ContainsKey(key))))
        {
            previous.TryGetValue(key, out var old);
            current.TryGetValue(key, out var now);
            var label = LineLabel(now ?? old!);
            if (Row("line", label, LineNode(old), LineNode(now)) is { } row)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    // Una línea quitada cuenta como ausente. La clave es el id de Shopify o, por posición, el lugar en el pedido.
    private static Dictionary<string, ShopifyLine> Keyed(IReadOnlyList<ShopifyLine> lines, bool byPosition)
    {
        var keyed = new Dictionary<string, ShopifyLine>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!line.IsRemoved)
            {
                keyed[!byPosition && line.Id is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"#{index}"] = line;
            }
        }

        return keyed;
    }

    private static JsonObject? LineNode(ShopifyLine? line) =>
        line is null
            ? null
            : new JsonObject
            {
                ["sku"] = Blank(line.Sku),
                ["title"] = Blank(line.Title),
                ["quantity"] = Normalized(line.Quantity),
                ["unitPrice"] = Normalized(line.OriginalUnitPrice),
                ["discountAmount"] = Normalized(line.DiscountAmount),
            };

    private static List<ShopifyEditRow> ShippingRows(ShopifyOrderSnapshot before, ShopifyOrderSnapshot after) =>
        Rows(
            Row("title", null, Text(ShopifyShippingMapper.CarrierTitle(before)), Text(ShopifyShippingMapper.CarrierTitle(after))),
            Row("amount", null, Normalized(ShopifyShippingMapper.Amount(before)), Normalized(ShopifyShippingMapper.Amount(after))),
            Row("storePickup", null, ShopifyShippingMapper.IsStorePickup(before), ShopifyShippingMapper.IsStorePickup(after)),
            Row(
                "cashOnDelivery",
                null,
                ShopifyCashOnDeliveryGateways.Matches(before.PaymentGateways),
                ShopifyCashOnDeliveryGateways.Matches(after.PaymentGateways)));

    private static List<ShopifyEditRow> AddressRows(ShopifyAddress? before, ShopifyAddress? after) =>
        Rows(
            Row("name", null, Text(before?.Name), Text(after?.Name)),
            Row("phone", null, Text(before?.Phone), Text(after?.Phone)),
            Row("address1", null, Text(before?.Address1), Text(after?.Address1)),
            Row("address2", null, Text(before?.Address2), Text(after?.Address2)),
            Row("city", null, Text(before?.City), Text(after?.City)),
            Row("province", null, Text(Province(before)), Text(Province(after))),
            // De un zip que un payload viejo no guardó no se sabe si cambió.
            before is { ZipUnrecorded: true } || after is { ZipUnrecorded: true }
                ? null
                : Row("zip", null, Text(before?.Zip), Text(after?.Zip)));

    private static List<ShopifyEditRow> BuyerRows(ShopifyOrderSnapshot before, ShopifyOrderSnapshot after, ShopifyStore store) =>
        Rows(
            Row("document", null, Text(Document(store, before)), Text(Document(store, after))),
            Row("email", null, Text(before.Buyer.Email), Text(after.Buyer.Email)),
            Row("phone", null, Text(before.Buyer.Phone), Text(after.Buyer.Phone)));

    private static List<ShopifyEditRow> AttributeRows(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var rows = new List<ShopifyEditRow>();
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var old = before.TryGetValue(key, out var a) ? a : null;
            var now = after.TryGetValue(key, out var b) ? b : null;
            if (!string.Equals(old, now, StringComparison.Ordinal))
            {
                rows.Add(new ShopifyEditRow("attribute", key, Text(old, trim: false), Text(now, trim: false)));
            }
        }

        return rows;
    }

    private static string? Document(ShopifyStore store, ShopifyOrderSnapshot snapshot) =>
        ShopifyCustomerResolver.DocumentOf(store, snapshot) is { } document ? $"{document.Type} {document.Number}" : null;

    private static string? Province(ShopifyAddress? address) => Blank(address?.Province) ?? Blank(address?.ProvinceCode);

    private static List<ShopifyEditRow> Rows(params ShopifyEditRow?[] rows) => [.. rows.OfType<ShopifyEditRow>()];

    private static ShopifyEditRow? Row(string field, string? label, JsonNode? before, JsonNode? after) =>
        JsonNode.DeepEquals(before, after) ? null : new ShopifyEditRow(field, label, before, after);

    private static JsonValue? Text(string? value, bool trim = true) =>
        (trim ? Blank(value) : value) is { } text ? JsonValue.Create(text) : null;

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
