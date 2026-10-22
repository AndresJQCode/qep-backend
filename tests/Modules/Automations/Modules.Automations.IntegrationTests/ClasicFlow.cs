using System.Text.Json.Nodes;

namespace Modules.Automations.IntegrationTests;

/// <summary>El flujo completo de Clasic del spec 2026-10-21 Anexo B, tal como se importa por <c>POST /chatbots/import</c>. El fixture
/// <c>clasic.json</c> trae <c>flow</c> y <c>layout</c>; las pruebas unitarias lo validan con el mismo archivo, enlazado.</summary>
internal static class ClasicFlow
{
    /// <summary>La conexión «API externa» que nombran todos sus bloques HTTP; el import la reescribe a la del tenant (D-C15).</summary>
    public static readonly Guid ClasicApi = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly JsonNode Fixture = ReadFixture();

    /// <summary>Sólo el miembro <c>flow</c>: lo que recibe <c>FlowJson.Parse</c> y el import.</summary>
    public static string FlowJson { get; } = Fixture["flow"]!.ToJsonString();

    /// <summary>El miembro <c>layout</c>: una posición por nodo, para que el editor lo abra legible.</summary>
    public static string LayoutJson { get; } = Fixture["layout"]!.ToJsonString();

    /// <summary>El flujo con otro <c>timeoutSeconds</c> en el bloque HTTP <paramref name="nodeId"/>.</summary>
    public static string WithTimeout(string nodeId, int seconds)
    {
        var flow = JsonNode.Parse(FlowJson)!;
        var node = flow["nodes"]!.AsArray().Single(candidate => candidate!["id"]!.GetValue<string>() == nodeId)!;
        node["timeoutSeconds"] = seconds;
        return flow.ToJsonString();
    }

    private static JsonNode ReadFixture()
    {
        var assembly = typeof(ClasicFlow).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("clasic.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No está el recurso {name}.");
        using var reader = new StreamReader(stream);
        return JsonNode.Parse(reader.ReadToEnd())!;
    }
}
