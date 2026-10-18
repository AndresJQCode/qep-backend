using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Admin;
using Modules.Shopify.Infrastructure.Parsing;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-10-15 §5.3: el cliente de la Admin API GraphQL de la conciliación contra un <see cref="HttpMessageHandler"/> falso.
/// La página de <c>orders(first: 50)</c> trae sólo ids (una consulta con las líneas anidadas para 50 pedidos pasaría el tope de
/// 1000 puntos por consulta de Shopify) y cada pedido se pide entero con <c>order(id:)</c>. Las fallas salen clasificadas
/// (<see cref="ShopifyOrdersSourceFailure"/>) y nunca con el token ni el cuerpo.
/// </summary>
public sealed class ShopifyGraphQlClientTests
{
    private const string Token = "shpat_super_secret_token";

    private static readonly ShopifyConnectionRef Connection =
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "954889", "mi-tienda.myshopify.com", true, Token);

    private static readonly DateTimeOffset Since = new(2026, 10, 18, 15, 55, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThrottledErrorInBodyMapsToThrottled()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """
            {"errors":[{"message":"Throttled","extensions":{"code":"THROTTLED","documentation":"https://shopify.dev/api/usage/rate-limits"}}],
             "extensions":{"cost":{"requestedQueryCost":52,"actualQueryCost":null,
               "throttleStatus":{"maximumAvailable":1000.0,"currentlyAvailable":12,"restoreRate":50.0}}}}
            """));

        var error = await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => bed.FetchAsync());

        Assert.Equal(ShopifyOrdersSourceFailure.Throttled, error.Failure);
        // Lo que falta para la consulta pedida, al ritmo de reposición: (52 − 12) / 50 = 0,8 s.
        Assert.Equal(TimeSpan.FromSeconds(0.8), error.RetryAfter);
    }

    [Fact]
    public async Task Status429WithRetryAfterMapsToThrottled()
    {
        var bed = new Bed((_, _) =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"errors":"Exceeded 2 calls per second for api client."}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "3.0");
            return response;
        });

        var error = await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => bed.FetchAsync());

        Assert.Equal(ShopifyOrdersSourceFailure.Throttled, error.Failure);
        Assert.Equal(TimeSpan.FromSeconds(3), error.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Status402MapsToRejected(HttpStatusCode status)
    {
        var bed = new Bed((_, _) => Json(status, """{"errors":"[API] Invalid API key or access token (unrecognized login or wrong password)"}"""));

        var error = await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => bed.FetchAsync());

        Assert.Equal(ShopifyOrdersSourceFailure.Rejected, error.Failure);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid API key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessDeniedErrorInBodyMapsToRejected()
    {
        var bed = new Bed((_, _) => Json(HttpStatusCode.OK, """{"errors":[{"message":"Access denied for orders field.","extensions":{"code":"ACCESS_DENIED"}}]}"""));

        var error = await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => bed.FetchAsync());

        Assert.Equal(ShopifyOrdersSourceFailure.Rejected, error.Failure);
    }

    [Fact]
    public async Task ServerErrorOrNetworkFailureMapsToUnavailable()
    {
        var down = new Bed((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var unreachable = new Bed((_, _) => throw new HttpRequestException($"Connection refused {Token}"));
        var unknownField = new Bed((_, _) => Json(HttpStatusCode.OK, """{"errors":[{"message":"Field 'foo' doesn't exist on type 'Order'","extensions":{"code":"undefinedField"}}]}"""));

        var errors = new[]
        {
            await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => down.FetchAsync()),
            await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => unreachable.FetchAsync()),
            await Assert.ThrowsAsync<ShopifyOrdersSourceException>(() => unknownField.FetchAsync()),
        };

        Assert.All(errors, error => Assert.Equal(ShopifyOrdersSourceFailure.Unavailable, error.Failure));
        // La excepción de red puede traer cualquier cosa en su mensaje: no viaja como InnerException ni en el texto.
        Assert.All(errors, error => Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchSendsTheWindowAndCursorAndReadsEachOrder()
    {
        var bed = new Bed((request, body) => body.Contains("orders(", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, Page(hasNextPage: true, "cursor-2", "gid://shopify/Order/5550000000001", "gid://shopify/Order/5550000000002"))
            : Json(HttpStatusCode.OK, Detail(Variable(body, "id"))));

        var page = await bed.Client.FetchUpdatedSinceAsync(Connection, Since, "cursor-1", Ct);

        Assert.Equal("cursor-2", page.NextCursor);
        Assert.Equal([5550000000001L, 5550000000002L], page.Orders.Select(order => order.OrderId));
        Assert.Equal(3, bed.Handler.Requests.Count);
        Assert.All(bed.Handler.Requests, request =>
        {
            Assert.Equal(new Uri("https://mi-tienda.myshopify.com/admin/api/2026-10/graphql.json"), request.Uri);
            Assert.Equal(Token, request.Token);
        });
        var list = bed.Handler.Requests[0].Body;
        Assert.Contains("orders(first: 50, after: $after, sortKey: UPDATED_AT, query: $query)", list, StringComparison.Ordinal);
        Assert.Equal("updated_at:>='2026-10-18T15:55:00Z'", Variable(list, "query"));
        Assert.Equal("cursor-1", Variable(list, "after"));
        Assert.Equal("gid://shopify/Order/5550000000002", Variable(bed.Handler.Requests[2].Body, "id"));
    }

    [Fact]
    public async Task FetchLastPageHasNoCursor()
    {
        var bed = new Bed((_, body) => body.Contains("orders(", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, Page(hasNextPage: false, "cursor-9"))
            : throw new InvalidOperationException("No order to read."));

        var page = await bed.Client.FetchUpdatedSinceAsync(Connection, Since, cursor: null, Ct);

        Assert.Null(page.NextCursor);
        Assert.Empty(page.Orders);
        Assert.Null(Variable(bed.Handler.Requests[0].Body, "after"));
    }

    // Un pedido con más líneas de las que caben en una consulta: las páginas siguientes de lineItems se piden aparte y se unen.
    [Fact]
    public async Task FetchMergesTheLinePagesOfALongOrder()
    {
        var bed = new Bed((_, body) =>
        {
            if (body.Contains("orders(", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Page(hasNextPage: false, null, "gid://shopify/Order/5550000000001"));
            }

            if (body.Contains("lineItems(first: 50, after: $after)", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """
                    {"data":{"order":{"lineItems":{"pageInfo":{"hasNextPage":false,"endCursor":"l2"},
                      "nodes":[{"sku":"GOR-002","quantity":1,"originalUnitPriceSet":{"shopMoney":{"amount":"30000.0"}},"discountAllocations":[]}]}}}}
                    """);
            }

            var order = JsonNode.Parse(Detail("gid://shopify/Order/5550000000001"))!;
            order["data"]!["order"]!["lineItems"]!["pageInfo"] = new JsonObject { ["hasNextPage"] = true, ["endCursor"] = "l1" };
            return Json(HttpStatusCode.OK, order.ToJsonString());
        });

        var page = await bed.Client.FetchUpdatedSinceAsync(Connection, Since, cursor: null, Ct);

        Assert.Equal(["CAM-001", "GOR-002"], Assert.Single(page.Orders).Lines.Select(line => line.Sku));
        Assert.Equal("l1", Variable(bed.Handler.Requests[2].Body, "after"));
    }

    // Un pedido que el parser no sabe leer no tumba la página: se salta (queda en el log con su id) y sigue con los demás. Uno que
    // se borró entre la lista y el detalle llega como null y también se salta.
    [Fact]
    public async Task UnreadableOrDeletedOrderIsSkipped()
    {
        var bed = new Bed((_, body) =>
        {
            if (body.Contains("orders(", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Page(false, null, "gid://shopify/Order/1", "gid://shopify/Order/2", "gid://shopify/Order/5550000000001"));
            }

            return Variable(body, "id") switch
            {
                "gid://shopify/Order/1" => Json(HttpStatusCode.OK, """{"data":{"order":{"id":"gid://shopify/Order/1","name":"#1","updatedAt":"ayer"}}}"""),
                "gid://shopify/Order/2" => Json(HttpStatusCode.OK, """{"data":{"order":null}}"""),
                var id => Json(HttpStatusCode.OK, Detail(id!)),
            };
        });

        var page = await bed.Client.FetchUpdatedSinceAsync(Connection, Since, cursor: null, Ct);

        Assert.Equal(5550000000001L, Assert.Single(page.Orders).OrderId);
    }

    // Antes de pedir un pedido, si lo que queda en el balde de Shopify no alcanza para lo que costó el anterior, espera lo que
    // tarda en reponerse (con TimeProvider, sin dormir) en vez de gastar la consulta en un THROTTLED.
    [Fact]
    public async Task LowBucketWaitsBeforeTheNextOrder()
    {
        var bed = new Bed((_, body) => body.Contains("orders(", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, Page(false, null, "gid://shopify/Order/5550000000001", "gid://shopify/Order/5550000000002"))
            : Json(HttpStatusCode.OK, Detail(Variable(body, "id"), requestedCost: 370, available: 270, restoreRate: 50)));

        await bed.Client.FetchUpdatedSinceAsync(Connection, Since, cursor: null, Ct);

        // El primer pedido no tiene costo conocido; el segundo necesita 370 y quedan 270: (370 − 270) / 50 = 2 s.
        Assert.Equal([TimeSpan.FromSeconds(2)], bed.Time.Delays);
    }

    [Fact]
    public async Task ConnectionWithoutTokenIsRefusedWithoutCallingShopify()
    {
        var bed = new Bed((_, _) => throw new InvalidOperationException("Shopify must not be called."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bed.Client.FetchUpdatedSinceAsync(Connection with { IsOperational = false, AccessToken = null }, Since, null, Ct));

        Assert.Empty(bed.Handler.Requests);
    }

    private static string Page(bool hasNextPage, string? endCursor, params string[] ids) =>
        new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["orders"] = new JsonObject
                {
                    ["pageInfo"] = new JsonObject { ["hasNextPage"] = hasNextPage, ["endCursor"] = endCursor },
                    ["nodes"] = new JsonArray([.. ids.Select(id => (JsonNode)new JsonObject { ["id"] = id })]),
                },
            },
        }.ToJsonString();

    /// <summary>El fixture de GraphQL de los parsers como respuesta de <c>order(id:)</c>, con ese id.</summary>
    private static string Detail(string? id, int? requestedCost = null, int? available = null, int? restoreRate = null)
    {
        var order = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphql", "taxes-included.json")))!;
        order["id"] = id;
        var response = new JsonObject { ["data"] = new JsonObject { ["order"] = order } };
        if (requestedCost is not null)
        {
            response["extensions"] = new JsonObject
            {
                ["cost"] = new JsonObject
                {
                    ["requestedQueryCost"] = requestedCost,
                    ["actualQueryCost"] = 20,
                    ["throttleStatus"] = new JsonObject
                    {
                        ["maximumAvailable"] = 1000,
                        ["currentlyAvailable"] = available,
                        ["restoreRate"] = restoreRate,
                    },
                },
            };
        }

        return response.ToJsonString();
    }

    private static string? Variable(string body, string name)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("variables", out var variables)
            && variables.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Bed
    {
        public Bed(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        {
            Handler = new FakeShopifyHandler(respond);
            Client = new ShopifyGraphQlClient(
                new FakeHttpClientFactory(Handler), new FixedApiVersion("2026-10"), Time, NullLogger<ShopifyGraphQlClient>.Instance);
        }

        public FakeShopifyHandler Handler { get; }

        public FakeTimeProvider Time { get; } = new();

        public ShopifyGraphQlClient Client { get; }

        public Task<ShopifyOrdersPage> FetchAsync() => Client.FetchUpdatedSinceAsync(Connection, Since, cursor: null, Ct);
    }
}

/// <summary>
/// Spec 2026-10-15 §5.3 y lo que dejó dicho la tarea 10: un campo que la consulta no pide llega como 0, <c>false</c> o
/// <c>null</c> sin error. Esta prueba arma una respuesta con <b>exactamente</b> los campos que pide la consulta real del cliente
/// (<see cref="ShopifyGraphQlClient.OrderQuery"/>), la pasa por <see cref="GraphQlOrderParser"/> y exige que no quede ningún
/// campo del snapshot vacío. Si alguien quita un campo de la consulta, o el parser empieza a leer uno que la consulta no pide, se
/// pone roja. Un campo nuevo en la consulta que esta prueba no sepa llenar también la pone roja: hay que decirle qué valor lleva.
/// </summary>
public sealed class ShopifyGraphQlQueryContractTests
{
    // Los campos que GraphQL devuelve como lista sin ser conexión (o el nodes de una conexión).
    private static readonly HashSet<string> ListFields = new(StringComparer.Ordinal)
    {
        "nodes", "customAttributes", "discountAllocations", "taxLines",
    };

    [Fact]
    public void OrderQueryRequestsEveryFieldTheParserReads()
    {
        var order = SampleOrder();

        var snapshot = GraphQlOrderParser.Parse(JsonSerializer.SerializeToElement(order));

        AssertPopulated(snapshot, "snapshot");
    }

    // El comprador sale primero del pedido y, sin él, del cliente: la consulta tiene que pedir el correo y el teléfono del
    // cliente de la versión de la API (defaultEmailAddress, defaultPhoneNumber; email y phone de Customer están deprecados).
    [Fact]
    public void OrderQueryRequestsTheCustomerContactForOrdersWithoutOne()
    {
        var order = SampleOrder();
        order.Remove("email");
        order.Remove("phone");

        var snapshot = GraphQlOrderParser.Parse(JsonSerializer.SerializeToElement(order));

        Assert.Equal("cliente@example.co", snapshot.Buyer.Email);
        Assert.Equal("+573009998877", snapshot.Buyer.Phone);
    }

    [Fact]
    public void OrderQueryAsksForRemovedShippingLines()
    {
        Assert.Contains("shippingLines(first: 10, includeRemovals: true)", ShopifyGraphQlClient.OrderQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("customer { id firstName lastName email", ShopifyGraphQlClient.OrderQuery, StringComparison.Ordinal);
    }

    private static JsonObject SampleOrder()
    {
        var root = Selection.Parse(ShopifyGraphQlClient.OrderQuery);
        var order = root.Children.Single(child => child.Name == "order");
        return (JsonObject)Build(order, parent: "query")!;
    }

    private static JsonNode? Build(Selection node, string parent)
    {
        JsonNode? value = node.Children.Count == 0
            ? Leaf(node.Name, parent)
            : new JsonObject(node.Children.Select(child => KeyValuePair.Create(child.Name, Build(child, node.Name))));
        return ListFields.Contains(node.Name) ? new JsonArray(value) : value;
    }

    private static JsonNode? Leaf(string name, string parent) => (name, parent) switch
    {
        ("id", "order") => "gid://shopify/Order/1001",
        ("id", "customer") => "gid://shopify/Customer/7",
        ("name", "order") => "#1001",
        ("name", "shippingAddress") => "Laura Gómez",
        ("email", "order") => "pedido@example.co",
        ("phone", "order") => "+573001112233",
        ("phone", "shippingAddress") => "+573001112244",
        ("emailAddress", "defaultEmailAddress") => "cliente@example.co",
        ("phoneNumber", "defaultPhoneNumber") => "+573009998877",
        ("createdAt", _) => "2026-10-01T15:00:00Z",
        ("updatedAt", _) => "2026-10-01T15:05:30Z",
        ("cancelledAt", _) => "2026-10-02T10:00:00Z",
        ("cancelReason", _) => "CUSTOMER",
        ("displayFinancialStatus", _) => "PARTIALLY_PAID",
        ("currencyCode", _) => "COP",
        ("amount", _) => "1234.5",
        ("taxesIncluded", _) => true,
        ("test", _) => true,
        ("note", _) => "Dejar en portería",
        ("paymentGatewayNames", _) => new JsonArray("Mercado Pago"),
        ("firstName", _) => "Laura",
        ("lastName", _) => "Gómez",
        ("address1", _) => "Calle 10 # 43-20",
        ("address2", _) => "Apto 301",
        ("city", _) => "Medellín",
        ("province", _) => "Antioquia",
        ("provinceCode", _) => "ANT",
        ("countryCodeV2", _) => "CO",
        ("key", _) => "documento",
        ("value", _) => "1036000111",
        ("sku", _) => "CAM-001",
        ("quantity", _) => 2,
        ("code", _) => "standard",
        ("title", _) => "Servientrega",
        ("isRemoved", _) => true,
        ("hasNextPage", _) => false,
        ("endCursor", _) => "cursor",
        _ => throw new InvalidOperationException($"The contract test does not know which value '{parent}.{name}' carries; add it."),
    };

    private static void AssertPopulated(object? value, string path)
    {
        switch (value)
        {
            case null:
                Assert.Fail($"{path} is null: the query does not request what the parser reads.");
                break;
            case string text:
                Assert.False(text.Length == 0, $"{path} is empty.");
                break;
            case bool flag:
                Assert.True(flag, $"{path} is false.");
                break;
            case decimal number:
                Assert.True(number != 0m, $"{path} is 0.");
                break;
            case long number:
                Assert.True(number != 0L, $"{path} is 0.");
                break;
            case DateTimeOffset instant:
                Assert.True(instant != default, $"{path} is the default instant.");
                break;
            case IReadOnlyDictionary<string, string> map:
                Assert.True(map.Count > 0, $"{path} is empty.");
                break;
            case System.Collections.IEnumerable items:
                var list = items.Cast<object?>().ToList();
                Assert.True(list.Count > 0, $"{path} is empty.");
                for (var index = 0; index < list.Count; index++)
                {
                    AssertPopulated(list[index], $"{path}[{index}]");
                }

                break;
            default:
                foreach (var property in value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    AssertPopulated(property.GetValue(value), $"{path}.{property.Name}");
                }

                break;
        }
    }

    /// <summary>Lo mínimo de GraphQL para leer la consulta del cliente: nombres y llaves; los argumentos entre paréntesis se
    /// saltan. La consulta no usa fragmentos ni alias a propósito.</summary>
    private sealed record Selection(string Name, List<Selection> Children)
    {
        public static Selection Parse(string query)
        {
            var tokens = Tokenize(query);
            var position = tokens.IndexOf("{");
            var root = new Selection("query", []);
            position++;
            ReadSet(tokens, ref position, root);
            return root;
        }

        private static void ReadSet(List<string> tokens, ref int position, Selection parent)
        {
            while (tokens[position] != "}")
            {
                var child = new Selection(tokens[position++], []);
                if (position < tokens.Count && tokens[position] == "{")
                {
                    position++;
                    ReadSet(tokens, ref position, child);
                }

                parent.Children.Add(child);
            }

            position++;
        }

        private static List<string> Tokenize(string query)
        {
            var tokens = new List<string>();
            var depth = 0;
            for (var index = 0; index < query.Length; index++)
            {
                var character = query[index];
                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')')
                {
                    depth--;
                }
                else if (depth > 0)
                {
                    continue;
                }
                else if (character is '{' or '}')
                {
                    tokens.Add(character.ToString());
                }
                else if (char.IsLetter(character) || character == '_')
                {
                    var start = index;
                    while (index + 1 < query.Length && (char.IsLetterOrDigit(query[index + 1]) || query[index + 1] == '_'))
                    {
                        index++;
                    }

                    tokens.Add(query[start..(index + 1)]);
                }
            }

            return tokens;
        }
    }
}

internal sealed class FixedApiVersion(string value) : IShopifyApiVersion
{
    public string Value { get; } = value;
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Shopify por HTTP: anota cada request (URL, token, cuerpo) y responde con <c>respond</c>.</summary>
internal sealed class FakeShopifyHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri Uri, string? Token, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var token = request.Headers.TryGetValues("X-Shopify-Access-Token", out var values) ? values.Single() : null;
        lock (Requests)
        {
            Requests.Add((request.RequestUri!, token, body));
        }

        return respond(request, body);
    }
}
