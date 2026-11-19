using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// El doble de la API de Siigo para las pruebas de integración (spec 2026-11-18 §11), compartido por las Tareas 6 a 10. Es un
/// <see cref="HttpMessageHandler"/> y no un doble del puerto <c>ISiigoApi</c>: se instala como el handler primario del cliente
/// nombrado <c>siigo</c>, así que cada prueba ejerce el cliente real (token, 401, caché de listas, forma de las peticiones) y sólo la
/// red es de mentira. Ninguna prueba llama a Siigo. Contesta por ruta con cuerpos con la forma del contrato y anota todas las
/// llamadas, para afirmar cuántas hubo y qué viajó.
/// </summary>
internal sealed class FakeSiigoApi
{
    public const string Token = "fake-siigo-token-SENTINEL";

    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<FakeSiigoCall, HttpResponseMessage>>> _scripted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<FakeSiigoCall, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeSiigoCall> _calls = new();

    public FakeSiigoApi()
    {
        On("/auth", HttpStatusCode.OK, $$"""{"access_token":"{{Token}}","expires_in":86400,"token_type":"Bearer","scope":"WholeApi"}""");
        On("/v1/document-types", HttpStatusCode.OK, """[{"id":24446,"code":"1","name":"Factura de venta","type":"FV","active":true}]""");
        On(
            "/v1/users",
            HttpStatusCode.OK,
            """{"pagination":{"page":1,"page_size":25,"total_results":1},"results":[{"id":629,"username":"ventas@empresa.test","first_name":"Ana","last_name":"Gomez","active":true}]}""");
        On("/v1/cost-centers", HttpStatusCode.OK, """[{"id":11,"code":"CC1","name":"Principal","active":true}]""");
        On("/v1/warehouses", HttpStatusCode.OK, """[{"id":3,"name":"Bodega central","active":true}]""");
        On("/v1/payment-types", HttpStatusCode.OK, """[{"id":5101,"name":"Transferencia","type":"Cash","active":true}]""");
        On(
            "/v1/taxes",
            HttpStatusCode.OK,
            """[{"id":13156,"name":"IVA 19%","type":"IVA","percentage":19.0,"active":true},{"id":13158,"name":"Retefuente 2.5%","type":"Retefuente","percentage":2.5,"active":true}]""");
        // Por defecto, el catálogo de productos que se arma con AddProduct; filtra por code= como Siigo.
        OnRequest("/v1/products", ProductsAnswer);
    }

    private readonly ConcurrentQueue<(string Id, string Code, string Name, bool Active)> _products = new();

    public IReadOnlyCollection<FakeSiigoCall> Calls => _calls;

    /// <summary>Un producto de Siigo que <c>GET /v1/products</c> devuelve (por código, si la petición lo trae).</summary>
    public void AddProduct(string id, string code, string name, bool active = true) => _products.Enqueue((id, code, name, active));

    private HttpResponseMessage ProductsAnswer(FakeSiigoCall call)
    {
        var code = System.Web.HttpUtility.ParseQueryString(call.Query)["code"];
        var found = _products.Where(product => code is null || string.Equals(product.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
        var results = string.Join(",", found.Select(product =>
            $$"""{"id":"{{product.Id}}","code":"{{product.Code}}","name":"{{product.Name}}","active":{{(product.Active ? "true" : "false")}}}"""));
        return Respond(HttpStatusCode.OK, $$"""{"pagination":{"page":1,"page_size":100,"total_results":{{found.Count}}},"results":[{{results}}]}""");
    }

    /// <summary>Cuántas veces se llamó a esa ruta (sin la query), con cualquier método.</summary>
    public int CallsTo(string path) => _calls.Count(call => call.Path == path);

    /// <summary>Contesta siempre así a esa ruta, hasta que se cambie.</summary>
    public void On(string path, HttpStatusCode status, string body) => _routes[path] = _ => Respond(status, body);

    /// <summary>Contesta en orden a las primeras llamadas a esa ruta; después, lo de <see cref="On"/>.</summary>
    public void OnSequence(string path, params (HttpStatusCode Status, string Body)[] answers)
    {
        var queue = new ConcurrentQueue<Func<FakeSiigoCall, HttpResponseMessage>>();
        foreach (var (status, body) in answers)
        {
            queue.Enqueue(_ => Respond(status, body));
        }

        _scripted[path] = queue;
    }

    public void OnRequest(string path, Func<FakeSiigoCall, HttpResponseMessage> answer) => _routes[path] = answer;

    /// <summary>El handler que se instala en el cliente nombrado. Uno nuevo por llamada: la fábrica de clientes dispone el que usó, y
    /// el doble (con sus llamadas anotadas) sigue vivo.</summary>
    public HttpMessageHandler CreateHandler() => new Forwarding(this);

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var call = new FakeSiigoCall(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, headers, body);
        _calls.Enqueue(call);

        if (_scripted.TryGetValue(call.Path, out var queue) && queue.TryDequeue(out var next))
        {
            return next(call);
        }

        return _routes.TryGetValue(call.Path, out var route)
            ? route(call)
            : Respond(HttpStatusCode.NotFound, """{"Errors":[{"Code":"not_found","Message":"No stub"}]}""");
    }

    public static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Forwarding(FakeSiigoApi fake) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            fake.HandleAsync(request, cancellationToken);
    }
}

internal sealed record FakeSiigoCall(HttpMethod Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}
