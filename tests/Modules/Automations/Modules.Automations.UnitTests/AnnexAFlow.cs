using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>
/// El flujo del Anexo A del spec 2026-10-21, completo: el anexo nombra n8, n10 y n11 pero no los escribe, y deja
/// «…» en textos, conexiones y URL. El archivo los completa para que el flujo sea válido (sólo avisos) y lo puedan
/// reusar las pruebas del guardado, del simulador y del motor.
/// </summary>
internal static class AnnexAFlow
{
    /// <summary>La conexión «API externa» de «Buscar cliente» (n3).</summary>
    public static readonly Guid CustomersApi = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>La conexión «API externa» de «Crear orden» (n12).</summary>
    public static readonly Guid ServiceOrdersApi = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static string Json { get; } = ReadFixture();

    public static FlowDefinition Parse() => FlowJson.Parse(Json);

    /// <summary>WhatsApp, las dos conexiones del anexo activas y horario en todas las conexiones.</summary>
    public static FlowValidationContext Context() => new(
        new HashSet<string> { "WhatsApp" },
        new HashSet<Guid> { CustomersApi, ServiceOrdersApi },
        new HashSet<Guid>(),
        AllConnectionsHaveBusinessHours: true);

    private static string ReadFixture()
    {
        var assembly = typeof(AnnexAFlow).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("annex-a.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No está el recurso {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
