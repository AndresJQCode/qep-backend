using System.Text.Json;
using System.Text.Json.Schema;
using Modules.Catalog.Application;

namespace Modules.Catalog.UnitTests;

public sealed class UpdateTaxRateRequestTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // /openapi/v1.json arma el esquema de cada cuerpo con JsonSchemaExporter. Un parámetro `JsonElement X = default` en el
    // constructor del record le daba un valor por defecto que no puede escribir, y el documento entero respondía 500.
    [Fact]
    public void ItsJsonSchemaCanBeExported()
    {
        var schema = Web.GetJsonSchemaAsNode(typeof(UpdateTaxRateRequest));

        Assert.NotNull(schema["properties"]?["minimumBase"]);
    }

    // Lo que el JsonElement existe para distinguir (spec impuestos por tipo §9.2): ausente conserva la base, null la quita.
    [Theory]
    [InlineData("""{"name":"ReteICA","percentage":0.966}""", JsonValueKind.Undefined)]
    [InlineData("""{"name":"ReteICA","percentage":0.966,"minimumBase":null}""", JsonValueKind.Null)]
    [InlineData("""{"name":"ReteICA","percentage":0.966,"minimumBase":4}""", JsonValueKind.Number)]
    public void AbsentNullAndNumberStayDistinct(string body, JsonValueKind expected)
    {
        var request = JsonSerializer.Deserialize<UpdateTaxRateRequest>(body, Web);

        Assert.NotNull(request);
        Assert.Equal(expected, request.MinimumBase.ValueKind);
    }
}
