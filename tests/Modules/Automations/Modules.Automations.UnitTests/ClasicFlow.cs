using System.Text.Json.Nodes;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>El flujo completo de Clasic (spec 2026-10-21 Anexo B): el mismo <c>clasic.json</c> que importa <c>ClasicFlowEndToEndTests</c>,
/// enlazado desde las pruebas de integración. Todos sus bloques HTTP usan una sola conexión «API externa», con un id fijo que la prueba
/// reescribe al importar (D-C15).</summary>
internal static class ClasicFlow
{
    public static readonly Guid ClasicApi = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static FlowDefinition Parse() => FlowJson.Parse(Document()["flow"]!.ToJsonString());

    /// <summary>Los ids con posición en el <c>layout</c> del fixture.</summary>
    public static IEnumerable<string> LayoutPositionIds() => Document()["layout"]!["positions"]!.AsObject().Select(pair => pair.Key);

    /// <summary>WhatsApp, la conexión de Clasic activa y sin horario (el bot de Clasic atiende 24/7).</summary>
    public static FlowValidationContext Context() => new(
        new HashSet<string> { "WhatsApp" },
        new HashSet<Guid> { ClasicApi },
        new HashSet<Guid>(),
        AllConnectionsHaveBusinessHours: false,
        HandoffMembers: new HashSet<Guid>());

    /// <summary>El archivo entero, tal como se manda en <c>export</c> al importar.</summary>
    public static string Raw()
    {
        var assembly = typeof(ClasicFlow).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("clasic.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No está el recurso {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonNode Document() => JsonNode.Parse(Raw())!;
}
