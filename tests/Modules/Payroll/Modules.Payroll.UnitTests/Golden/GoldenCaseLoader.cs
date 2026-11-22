using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests.Golden;

/// <summary>Lee los casos de oro versionados bajo <c>Golden/synthetic</c> y <c>Golden/siigo</c> y sus archivos legales de <c>Golden/legal</c>.</summary>
internal static class GoldenCaseLoader
{
    private static readonly string[] CaseFolders = ["synthetic", "siigo"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Root => Path.Combine(AppContext.BaseDirectory, "Golden");

    /// <summary>Todos los casos, ordenados por carpeta y nombre de archivo.</summary>
    public static IReadOnlyList<GoldenCase> All() =>
        CaseFolders
            .Select(folder => Path.Combine(Root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
            .Select(LoadFile)
            .ToList();

    public static GoldenCase Load(string name) => All().Single(golden => golden.Name == name);

    public static LegalValueSet LoadLegal(string relativePath)
    {
        var json = File.ReadAllText(Path.Combine(Root, relativePath));
        return JsonSerializer.Deserialize<LegalValueSet>(json, Options)
            ?? throw new InvalidOperationException($"Empty legal file {relativePath}.");
    }

    private static GoldenCase LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GoldenCase>(json, Options)
            ?? throw new InvalidOperationException($"Empty golden case {path}.");
    }
}
