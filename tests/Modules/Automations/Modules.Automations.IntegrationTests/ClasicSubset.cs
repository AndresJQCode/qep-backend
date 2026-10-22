using System.Text.Json.Nodes;

namespace Modules.Automations.IntegrationTests;

/// <summary>El subconjunto del flujo de Clasic del spec 2026-10-21 Anexo B (pasos 4, 5, 7, 9, 10 y 11, con el subflujo de cliente nuevo del
/// paso 6), tal como se importa por <c>POST /chatbots/import</c>. Las pruebas unitarias lo validan con el mismo archivo, enlazado.</summary>
internal static class ClasicSubset
{
    /// <summary>La conexión «API externa» que nombran todos sus bloques HTTP; el import la reescribe a la del tenant (D-C15).</summary>
    public static readonly Guid ClasicApi = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static string Json { get; } = ReadFixture();

    /// <summary>El flujo con otro <c>timeoutSeconds</c> en el bloque HTTP <paramref name="nodeId"/>.</summary>
    public static string WithTimeout(string nodeId, int seconds)
    {
        var flow = JsonNode.Parse(Json)!;
        var node = flow["nodes"]!.AsArray().Single(candidate => candidate!["id"]!.GetValue<string>() == nodeId)!;
        node["timeoutSeconds"] = seconds;
        return flow.ToJsonString();
    }

    private static string ReadFixture()
    {
        var assembly = typeof(ClasicSubset).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("clasic-subset.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No está el recurso {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
