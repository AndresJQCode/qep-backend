using System.Text.Json.Nodes;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-10-21 D-H4 y fallos P2-R11 y P2-R31: lo que el bloque HTTP va a mandar, armado de las variables de la corrida sin tocar la
/// red. Un valor del cliente en la ruta va escapado (no puede cambiar la ruta ni agregar query) y en el cuerpo JSON va como JSON.
/// </summary>
public sealed class HttpBlockRequestTests
{
    private static RunVariables Variables()
    {
        var variables = RunVariables.Empty();
        variables.Set("cliente", new JsonObject { ["id"] = 7, ["nombre"] = "Ana" });
        variables.Set("cedula", "10203");
        return variables;
    }

    internal static HttpNode Node(
        string path = "/customers",
        string method = "GET",
        IReadOnlyList<KeyValue>? query = null,
        HttpBody? body = null,
        IReadOnlyList<HttpMapping>? mapping = null,
        IReadOnlyList<int>? okWhen = null,
        int timeoutSeconds = 8,
        Guid? connectionId = null,
        bool withoutConnection = false) =>
        new(
            "http1", "Consultar cliente", new Dictionary<string, string?> { ["ok"] = null, ["error"] = null },
            withoutConnection ? null : connectionId ?? HttpBlockExecutorTests.ConnectionId, method, path, query ?? [], body, timeoutSeconds,
            okWhen, mapping ?? [], null);

    [Fact]
    public void InterpolatesPathQueryAndJsonBody()
    {
        var node = Node(
            "/customers/{cliente.id}", "POST", [new KeyValue("identificationNumber", "{cedula}")],
            new HttpBody(HttpBodyKinds.Json, """{ "customerId": {cliente.id}, "name": "{cliente.nombre}" }""", null, null, null));

        var request = HttpBlockRequest.Build(node, Variables());

        Assert.Equal("POST", request.Method);
        Assert.Equal("/customers/7", request.Path);
        Assert.Equal([new KeyValuePair<string, string>("identificationNumber", "10203")], request.Query);
        var json = Assert.IsType<HttpBlockBody.Json>(request.Body);
        var parsed = JsonNode.Parse(json.Content)!;
        Assert.Equal(7, parsed["customerId"]!.GetValue<int>());
        Assert.Equal("Ana", parsed["name"]!.GetValue<string>());
    }

    [Fact]
    public void MultipartCarriesFieldsAndTheFileVariable()
    {
        var node = Node(
            "/customers/new-customer", "POST",
            body: new HttpBody(HttpBodyKinds.Multipart, null, [new KeyValue("identificationNumber", "{cedula}")], "file", "factura"));

        var request = HttpBlockRequest.Build(node, Variables());

        var multipart = Assert.IsType<HttpBlockBody.Multipart>(request.Body);
        Assert.Equal([new KeyValuePair<string, string>("identificationNumber", "10203")], multipart.Fields);
        Assert.Equal("file", multipart.FileField);
        Assert.Equal("factura", multipart.FileVariable);
    }

    /// <summary>P2-R11: dentro de comillas el valor va escapado como texto JSON; suelto, como su literal JSON (y <c>null</c> si falta).</summary>
    [Fact]
    public void AJsonBodyEscapesStringsAndWritesBareValuesAsJsonLiterals()
    {
        var variables = Variables();
        variables.Set("nota", "Dijo \"hola\" y C:\\ruta");
        variables.Set("monto", 1500.5m);
        var template = """
            { "nota": "Nota: {nota}", "suelta": {nota}, "monto": {monto}, "falta": {nada}, "cliente": {cliente}, "dentro": "{cliente}" }
            """;
        var node = Node(method: "POST", body: new HttpBody(HttpBodyKinds.Json, template, null, null, null));

        var json = Assert.IsType<HttpBlockBody.Json>(HttpBlockRequest.Build(node, variables).Body);

        var parsed = JsonNode.Parse(json.Content)!.AsObject();
        Assert.Equal("Nota: Dijo \"hola\" y C:\\ruta", parsed["nota"]!.GetValue<string>());
        Assert.Equal("Dijo \"hola\" y C:\\ruta", parsed["suelta"]!.GetValue<string>());
        Assert.Equal(1500.5m, parsed["monto"]!.GetValue<decimal>());
        Assert.True(parsed.ContainsKey("falta"));
        Assert.Null(parsed["falta"]);
        Assert.Equal(7, parsed["cliente"]!["id"]!.GetValue<int>());
        Assert.Equal("""{"id":7,"nombre":"Ana"}""", parsed["dentro"]!.GetValue<string>());
    }

    /// <summary>P2-R11: una comilla escapada en el texto de la plantilla no cierra la cadena.</summary>
    [Fact]
    public void AnEscapedQuoteInTheTemplateKeepsTheTokenInsideTheString()
    {
        var variables = Variables();
        variables.Set("nota", "a\"b");
        var node = Node(method: "POST", body: new HttpBody(HttpBodyKinds.Json, """{ "x": "dijo \"{nota}\"" }""", null, null, null));

        var json = Assert.IsType<HttpBlockBody.Json>(HttpBlockRequest.Build(node, variables).Body);

        Assert.Equal("dijo \"a\"b\"", JsonNode.Parse(json.Content)!["x"]!.GetValue<string>());
    }

    /// <summary>P2-R31: el valor del cliente va escapado; el texto de la plantilla no.</summary>
    [Fact]
    public void PathValuesAreEscapedButTheTemplateTextIsNot()
    {
        var variables = Variables();
        variables.Set("id", "a/b?c#d");

        var request = HttpBlockRequest.Build(Node("/customers/{id}/all-items"), variables);

        Assert.Equal("/customers/a%2Fb%3Fc%23d/all-items", request.Path);
        Assert.Null(request.PathRejection);
    }

    /// <summary>
    /// P2-R31/P2-R34: un valor con un segmento <c>.</c> o <c>..</c> (partido por <c>/</c> y <c>\</c>, ya desescapado) no sale, aunque escapado
    /// quede en un solo segmento: un proxy que decodifica <c>%2F</c> y normaliza (nginx con URI en <c>proxy_pass</c>) subiría igual. Y
    /// <c>System.Uri</c> resuelve <c>..</c> y <c>%2E%2E</c> hacia arriba.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../x?y#z")]
    [InlineData("../admin")]
    [InlineData("..\\admin")]
    [InlineData("a/./b")]
    [InlineData("%2E%2E%2Fadmin")]
    [InlineData("%252E%252E%252Fadmin")]
    public void AValueWithADotSegmentIsRejected(string value)
    {
        var variables = Variables();
        variables.Set("id", value);

        Assert.Equal("path_dot_segment", HttpBlockRequest.Build(Node("/customers/{id}"), variables).PathRejection);
    }

    /// <summary>P2-R32: dos valores que juntos forman <c>..</c> también se ven en la ruta ya interpolada.</summary>
    [Fact]
    public void TwoValuesThatTogetherMakeADotSegmentAreRejected()
    {
        var variables = Variables();
        variables.Set("a", ".");
        variables.Set("b", ".");

        Assert.Equal("path_dot_segment", HttpBlockRequest.Build(Node("/customers/{a}{b}"), variables).PathRejection);
    }

    /// <summary>P2-R34: <c>/customers/{id}</c> sin id llamaría a la colección (un PUT o PATCH sobre todas).</summary>
    [Fact]
    public void AMissingPathValueIsRejected()
    {
        Assert.Equal("path_value_empty", HttpBlockRequest.Build(Node("/customers/{nadie}"), Variables()).PathRejection);
    }

    /// <summary>Ruling P2-R46: un valor que sólo tiene barras se escapa a <c>%2F</c>, pero un proxy que decodifica antes de reenviar ve
    /// <c>/customers//</c> y cae en la colección, como el valor vacío de P2-R34.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("//")]
    [InlineData("/\\/")]
    [InlineData("%2F")]
    [InlineData("%5C%2F")]
    public void AValueMadeOnlyOfSlashesIsEmpty(string value)
    {
        var variables = Variables();
        variables.Set("id", value);

        Assert.Equal("path_value_empty", HttpBlockRequest.Build(Node("/customers/{id}"), variables).PathRejection);
    }

    [Fact]
    public void ADotInsideAValueIsFine()
    {
        var variables = Variables();
        variables.Set("id", "v1.2..3");

        Assert.Null(HttpBlockRequest.Build(Node("/customers/{id}"), variables).PathRejection);
    }
}
