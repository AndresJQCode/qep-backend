using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>El subconjunto del flujo de Clasic (spec 2026-10-21 Anexo B) que importa <c>ClasicFlowEndToEndTests</c>: el mismo archivo,
/// enlazado desde las pruebas de integración. Todos sus bloques HTTP usan una sola conexión «API externa», con un id fijo que la prueba
/// reescribe al importar (D-C15).</summary>
internal static class ClasicSubsetFlow
{
    public static readonly Guid ClasicApi = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static FlowDefinition Parse()
    {
        var assembly = typeof(ClasicSubsetFlow).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("clasic-subset.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No está el recurso {name}.");
        using var reader = new StreamReader(stream);
        return FlowJson.Parse(reader.ReadToEnd());
    }

    /// <summary>WhatsApp, la conexión de Clasic activa y sin horario (el bot de Clasic atiende 24/7).</summary>
    public static FlowValidationContext Context() => new(
        new HashSet<string> { "WhatsApp" },
        new HashSet<Guid> { ClasicApi },
        new HashSet<Guid>(),
        AllConnectionsHaveBusinessHours: false);
}
