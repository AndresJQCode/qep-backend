namespace Modules.Automations.IntegrationTests;

/// <summary>El flujo del Anexo A del spec 2026-10-21, el mismo archivo de <c>AnnexAFlow</c> de las pruebas unitarias (enlazado en el
/// csproj). Sólo deja avisos si las dos conexiones «API externa» que nombra están activas (<see cref="AutomationsApiHarness.FakeExternalApis"/>).</summary>
internal static class AnnexA
{
    /// <summary>La conexión «API externa» de «Buscar cliente» (n3).</summary>
    public static readonly Guid CustomersApi = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>La conexión «API externa» de «Crear orden» (n12).</summary>
    public static readonly Guid ServiceOrdersApi = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static string Json { get; } = ReadFixture();

    private static string ReadFixture()
    {
        var assembly = typeof(AnnexA).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith("annex-a.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"No está el recurso {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
