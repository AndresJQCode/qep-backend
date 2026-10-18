using System.Text.Json;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Parsing;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-10-15 §5.2, §5.3 y §13: el webhook trae el pedido en REST y la conciliación en GraphQL, y los dos tienen que
/// dar el mismo <see cref="ShopifyOrderSnapshot"/>; si no, un pedido importado por webhook cambiaría al conciliarse sin que Shopify
/// lo haya tocado. Los fixtures son sintéticos con la forma documentada (<c>_source</c>); V-9 los reemplaza por reales.</summary>
public sealed class OrderParsersTests
{
    private static readonly IReadOnlyList<string> NoGateways = [];
    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();
    private static readonly IReadOnlyList<ShopifyLine> NoLines = [];
    private static readonly IReadOnlyList<ShopifyShippingLine> NoShippingLines = [];

    public static TheoryData<string> Cases =>
    [
        "taxes-included", "taxes-excluded", "discounts", "pickup", "shipping-charged", "shipping-free-by-discount",
        "shipping-removed-line", "cash-on-delivery", "test-order", "cancelled", "no-document", "no-city",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void RestAndGraphQlSameOrderProduceEqualSnapshots(string fixture)
    {
        var rest = RestOrderParser.Parse(Fixture("rest", fixture));
        var graphQl = GraphQlOrderParser.Parse(Fixture("graphql", fixture));

        AssertSameSnapshot(rest, graphQl);
        AssertCase(fixture, rest);
    }

    [Fact]
    public void ParseTaxesIncludedReadsEveryField()
    {
        var snapshot = RestOrderParser.Parse(Fixture("rest", "taxes-included"));

        var expected = new ShopifyOrderSnapshot(
            5_550_000_000_001L, "#1001", 7_770_000_000_001L,
            new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 1, 15, 5, 30, TimeSpan.Zero),
            null, null, "paid", "COP", 119_000m, true, false, null,
            ["Mercado Pago"],
            new ShopifyBuyer("Laura", "Gómez", "laura.gomez@example.com", "+573001112233"),
            new ShopifyAddress("Laura Gómez", "Calle 10 # 43-20", "Apto 301", "Medellín", "Antioquia", "ANT", "CO", "+573001112233"),
            new Dictionary<string, string> { ["documento"] = "1036000111", ["tipo_documento"] = "CC" },
            [new ShopifyLine("CAM-001", 2m, 50_000m, 0m)],
            [new ShopifyShippingLine("standard", "Servientrega", 19_000m, 3_033.61m, false)]);
        AssertSameSnapshot(expected, snapshot);
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAt.Offset);
    }

    public static TheoryData<string, string> WithoutCustomerAndNullSku => new()
    {
        {
            "rest",
            """
            {"id": 42, "name": "#2001", "created_at": "2026-10-01T10:00:00-05:00", "updated_at": "2026-10-01T10:00:00-05:00",
             "currency": "COP", "total_price_set": {"shop_money": {"amount": "50000.00"}}, "financial_status": null,
             "email": null, "phone": null, "note": null, "customer": null, "shipping_address": null,
             "payment_gateway_names": null, "note_attributes": null,
             "line_items": [{"sku": null, "quantity": 1, "price_set": {"shop_money": {"amount": "50000.00"}}, "discount_allocations": null},
                            {"sku": "", "quantity": 1, "price": "0.00"}],
             "shipping_lines": []}
            """
        },
        {
            "graphql",
            """
            {"id": "gid://shopify/Order/42", "name": "#2001", "createdAt": "2026-10-01T15:00:00Z", "updatedAt": "2026-10-01T15:00:00Z",
             "currencyCode": "COP", "totalPriceSet": {"shopMoney": {"amount": "50000.0"}}, "displayFinancialStatus": null,
             "email": null, "phone": null, "note": null, "customer": null, "shippingAddress": null,
             "paymentGatewayNames": [], "customAttributes": [],
             "lineItems": {"nodes": [{"sku": null, "quantity": 1, "originalUnitPriceSet": {"shopMoney": {"amount": "50000.0"}}, "discountAllocations": []},
                                     {"sku": "", "quantity": 1, "originalUnitPriceSet": {"shopMoney": {"amount": "0.0"}}}]},
             "shippingLines": {"nodes": []}}
            """
        },
    };

    // Un cuerpo con firma válida y campos nulos no puede tumbar el worker; el importador decide después
    // (product_unmatched por la línea sin SKU, cliente incompleto por el comprador sin cuenta).
    [Theory]
    [MemberData(nameof(WithoutCustomerAndNullSku))]
    public void ParseOrderWithoutCustomerAndNullSkuProducesSnapshot(string format, string json)
    {
        var snapshot = Parse(format, json);

        Assert.Equal((42L, (long?)null, (ShopifyAddress?)null), (snapshot.OrderId, snapshot.CustomerId, snapshot.ShippingAddress));
        Assert.Equal(new ShopifyBuyer(null, null, null, null), snapshot.Buyer);
        Assert.Equal([new ShopifyLine(null, 1m, 50_000m, 0m), new ShopifyLine(null, 1m, 0m, 0m)], snapshot.Lines);
        Assert.Empty(snapshot.ShippingLines);
        Assert.Empty(snapshot.PaymentGateways);
        Assert.Empty(snapshot.Attributes);
        Assert.Equal(string.Empty, snapshot.FinancialStatus);
        Assert.Null(snapshot.ToHeader().FinancialStatus);
    }

    [Theory]
    [InlineData("rest", """{"id": 1, "name": "#1", "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0", "line_items": []}""")]
    [InlineData("graphql", """{"id": "gid://shopify/Order/1", "name": "#1", "createdAt": "2026-10-01T10:00:00Z", "updatedAt": "2026-10-01T10:00:00Z", "currencyCode": "COP", "totalPriceSet": {"shopMoney": {"amount": "0"}}, "lineItems": {"nodes": []}}""")]
    public void ParseWithEmptyLineItemsProducesSnapshot(string format, string json)
    {
        var snapshot = Parse(format, json);

        Assert.Empty(snapshot.Lines);
        Assert.Empty(snapshot.ShippingLines);
    }

    [Fact]
    public void ParseAttributesReadsNoteAttributes()
    {
        var rest = RestOrderParser.Parse(Json("""
            {"id": 1, "name": "#1", "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0",
             "note_attributes": [{"name": "documento", "value": " 900123456 "}, {"name": "tipo_documento", "value": "NIT"},
                                 {"name": "sin_valor", "value": null}, {"name": null, "value": "huérfano"}]}
            """));
        var graphQl = GraphQlOrderParser.Parse(Json("""
            {"id": "gid://shopify/Order/1", "name": "#1", "createdAt": "2026-10-01T10:00:00Z", "updatedAt": "2026-10-01T10:00:00Z", "currencyCode": "COP",
             "totalPriceSet": {"shopMoney": {"amount": "0"}},
             "customAttributes": [{"key": "documento", "value": " 900123456 "}, {"key": "tipo_documento", "value": "NIT"},
                                  {"key": "sin_valor", "value": null}, {"key": null, "value": "huérfano"}]}
            """));

        // El valor viaja tal cual (recortar el documento es cosa del importador, §6.2.1); un atributo sin nombre no tiene clave.
        var expected = new Dictionary<string, string> { ["documento"] = " 900123456 ", ["tipo_documento"] = "NIT", ["sin_valor"] = string.Empty };
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), rest.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal), graphQl.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    public static TheoryData<string, string, string> MissingRequired => new()
    {
        { "rest", "id", """{"name": "#1", "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0"}""" },
        { "rest", "name", """{"id": 1, "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0"}""" },
        { "rest", "updated_at", """{"id": 1, "name": "#1", "created_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0"}""" },
        { "rest", "updated_at", """{"id": 1, "name": "#1", "created_at": "2026-10-01T10:00:00Z", "updated_at": "ayer", "currency": "COP", "total_price": "0"}""" },
        { "rest", "id", """{"id": "gid://shopify/Order/abc", "name": "#1", "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0"}""" },
        { "rest", "line_items[0].quantity", """{"id": 1, "name": "#1", "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "COP", "total_price": "0", "line_items": [{"sku": "A", "quantity": "dos", "price": "1"}]}""" },
        { "graphql", "id", """{"name": "#1", "createdAt": "2026-10-01T10:00:00Z", "updatedAt": "2026-10-01T10:00:00Z", "currencyCode": "COP", "totalPriceSet": {"shopMoney": {"amount": "0"}}}""" },
        { "graphql", "id", """{"id": "gid://shopify/Customer/1", "name": "#1", "createdAt": "2026-10-01T10:00:00Z", "updatedAt": "2026-10-01T10:00:00Z", "currencyCode": "COP", "totalPriceSet": {"shopMoney": {"amount": "0"}}}""" },
        { "graphql", "name", """{"id": "gid://shopify/Order/1", "name": null, "createdAt": "2026-10-01T10:00:00Z", "updatedAt": "2026-10-01T10:00:00Z", "currencyCode": "COP", "totalPriceSet": {"shopMoney": {"amount": "0"}}}""" },
        { "graphql", "updatedAt", """{"id": "gid://shopify/Order/1", "name": "#1", "createdAt": "2026-10-01T10:00:00Z", "currencyCode": "COP", "totalPriceSet": {"shopMoney": {"amount": "0"}}}""" },
        { "graphql", "totalPriceSet.shopMoney.amount", """{"id": "gid://shopify/Order/1", "name": "#1", "createdAt": "2026-10-01T10:00:00Z", "updatedAt": "2026-10-01T10:00:00Z", "currencyCode": "COP"}""" },
    };

    // Sin id, nombre o fecha de actualización no hay fila del espejo que ubicar ni orden entre snapshots (§5.4): falla de payload
    // tipada, que el procesador de entregas marca como procesada con error en vez de reintentar.
    [Theory]
    [MemberData(nameof(MissingRequired))]
    public void ParseWithoutRequiredFieldThrowsPayloadException(string format, string field, string json)
    {
        var error = Assert.Throws<ShopifyPayloadException>(() => Parse(format, json));

        Assert.Equal(field, error.Field);
    }

    [Theory]
    [InlineData("rest", "[]")]
    [InlineData("graphql", "null")]
    public void ParseNonObjectThrowsPayloadException(string format, string json)
    {
        Assert.Throws<ShopifyPayloadException>(() => Parse(format, json));
    }

    // El parser no recorta ni valida anchos. Deja el valor tal cual y la cabecera la valida el dominio
    // (ShopifyOrderMirror.Validated), que es el dueño de las constantes; el ArgumentException que tira es el fallo permanente.
    [Fact]
    public void ParseOverlongNameKeepsItAndTheDomainRejectsTheHeader()
    {
        var name = "#" + new string('9', ShopifyOrderMirror.OrderNameMaxLength);
        var snapshot = RestOrderParser.Parse(Json($$"""
            {"id": 1, "name": "{{name}}", "created_at": "2026-10-01T10:00:00Z", "updated_at": "2026-10-01T10:00:00Z", "currency": "cop", "total_price": "0"}
            """));

        Assert.Equal(name, snapshot.Name);
        Assert.Throws<ArgumentException>(() =>
            ShopifyOrderMirror.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), snapshot.OrderId, "{}", snapshot.UpdatedAt, snapshot.ToHeader()));
    }

    [Fact]
    public void ToHeaderCarriesTheMirrorColumns()
    {
        var snapshot = GraphQlOrderParser.Parse(Fixture("graphql", "cancelled"));

        Assert.Equal(
            new ShopifyOrderHeader("#1010", 7_770_000_000_001L, snapshot.CreatedAt, new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero), "voided", "COP", 119_000m, true),
            snapshot.ToHeader());
    }

    private static void AssertCase(string fixture, ShopifyOrderSnapshot snapshot)
    {
        switch (fixture)
        {
            case "taxes-excluded":
                Assert.Equal((false, 137_360m, 100_000m), (snapshot.TaxesIncluded, snapshot.TotalPrice, snapshot.Lines[0].OriginalUnitPrice));
                break;
            case "discounts":
                // shop_money, no presentment_money: el comprador pagó en USD pero la tienda cuenta en COP.
                Assert.Equal(96_000m, snapshot.TotalPrice);
                Assert.Equal([new ShopifyLine("CAM-001", 2m, 50_000m, 10_000m), new ShopifyLine("GOR-002", 1m, 20_000m, 4_000m)], snapshot.Lines);
                break;
            case "pickup":
                Assert.Equal([new ShopifyShippingLine("pickup", "Recogida en tienda", 0m, 0m, false)], snapshot.ShippingLines);
                break;
            case "shipping-charged":
                Assert.Equal([new ShopifyShippingLine("interrapidisimo", "Inter Rapidísimo", 19_999.50m, 3_193.20m, false)], snapshot.ShippingLines);
                break;
            case "shipping-free-by-discount":
                Assert.Equal(0m, Assert.Single(snapshot.ShippingLines).DiscountedPrice);
                break;
            case "shipping-removed-line":
                // El parser entrega las retiradas marcadas; descartarlas es del importador (§6.2.2).
                Assert.Equal([true, false], snapshot.ShippingLines.Select(line => line.IsRemoved));
                break;
            case "cash-on-delivery":
                Assert.Equal(("pending", "Cash on Delivery (COD)"), (snapshot.FinancialStatus, Assert.Single(snapshot.PaymentGateways)));
                break;
            case "test-order":
                Assert.True(snapshot.Test);
                break;
            case "cancelled":
                Assert.Equal((new DateTimeOffset(2026, 10, 2, 14, 0, 0, TimeSpan.Zero), "customer", "voided"), (snapshot.CancelledAt!.Value, snapshot.CancelReason!, snapshot.FinancialStatus));
                Assert.Equal("El cliente pidió cancelar por WhatsApp", snapshot.Note);
                break;
            case "no-document":
                Assert.Empty(snapshot.Attributes);
                break;
            case "no-city":
                Assert.Equal(((string?)null, (string?)null, "ANT"), (snapshot.ShippingAddress!.City, snapshot.ShippingAddress.Address2, snapshot.ShippingAddress.ProvinceCode));
                Assert.Single(snapshot.Lines);
                break;
            default:
                Assert.Equal(("paid", false, false), (snapshot.FinancialStatus, snapshot.Test, snapshot.CancelledAt.HasValue));
                break;
        }
    }

    // El record compara sus colecciones por referencia: se comparan aparte, en orden, y el resto con ellas neutralizadas.
    private static void AssertSameSnapshot(ShopifyOrderSnapshot expected, ShopifyOrderSnapshot actual)
    {
        Assert.Equal(expected.PaymentGateways, actual.PaymentGateways);
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.ShippingLines, actual.ShippingLines);
        Assert.Equal(
            expected.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actual.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(WithoutCollections(expected), WithoutCollections(actual));
    }

    private static ShopifyOrderSnapshot WithoutCollections(ShopifyOrderSnapshot snapshot) =>
        snapshot with { PaymentGateways = NoGateways, Attributes = NoAttributes, Lines = NoLines, ShippingLines = NoShippingLines };

    private static ShopifyOrderSnapshot Parse(string format, string json) =>
        format == "rest" ? RestOrderParser.Parse(Json(json)) : GraphQlOrderParser.Parse(Json(json));

    private static JsonElement Fixture(string format, string name) =>
        Json(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", format, name + ".json")));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
