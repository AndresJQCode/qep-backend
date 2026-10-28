using System.Reflection;
using System.Text.Json;
using Modules.Automations.Application;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Infrastructure.Seed;

/// <summary>
/// Spec 2026-10-21 D-C8 y §4.5: las plantillas del sistema son los <c>Seed/Templates/*.json</c> embebidos en este ensamblado (como los
/// seeds de Geografía y Catálogo, <c>GeographySeeder.OpenResource</c>), un archivo por plantilla. Se leen una vez, al construir el
/// singleton, y <c>AutomationsDatabaseInitializer</c> lo construye al arrancar el host. Un archivo roto (o ningún archivo: el glob del
/// csproj o el espacio de nombres cambió) es un error de quien lo escribió, no del tenant: lanza <see cref="InvalidOperationException"/>
/// y el host no arranca, en vez de dejar la galería vacía o rota hasta el primer pedido. <c>SystemTemplatesTests</c> lo atrapa antes,
/// con <see cref="FlowValidator"/>.
/// </summary>
internal sealed class SystemTemplateCatalog : ISystemTemplateCatalog
{
    private const string ResourceMarker = ".Seed.Templates.";

    private readonly Dictionary<string, SystemTemplate> _byId;

    public SystemTemplateCatalog()
        : this(typeof(SystemTemplateCatalog).Assembly)
    {
    }

    /// <summary>Con otro ensamblado sólo para la prueba de que un catálogo sin recursos no arranca.</summary>
    internal SystemTemplateCatalog(Assembly assembly)
    {
        // Por nombre de recurso: el orden no depende del sistema de archivos de quien compiló.
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (resources.Count == 0)
        {
            throw new InvalidOperationException(
                $"El ensamblado «{assembly.GetName().Name}» no trae plantillas del sistema (recursos *{ResourceMarker}*.json): revisa el " +
                "EmbeddedResource del csproj.");
        }

        All = [.. resources.Select(name => Read(assembly, name))];

        _byId = new Dictionary<string, SystemTemplate>(StringComparer.Ordinal);
        foreach (var template in All)
        {
            if (!_byId.TryAdd(template.Id, template))
            {
                throw new InvalidOperationException($"Hay dos plantillas del sistema con el id «{template.Id}».");
            }
        }
    }

    public IReadOnlyList<SystemTemplate> All { get; }

    public SystemTemplate? Find(string id) => _byId.GetValueOrDefault(id);

    private static SystemTemplate Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"No se pudo abrir el recurso embebido «{resourceName}».");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var category = RequiredString(root, "category", resourceName);
        if (!ChatbotTemplateCategories.All.Contains(category))
        {
            throw new InvalidOperationException($"La plantilla «{resourceName}» tiene la categoría «{category}», que no existe.");
        }

        // En la forma del modelo, como lo guarda un borrador: el chatbot instalado y el que se edita a mano se leen igual.
        var flow = FlowJson.Parse(Required(root, "flow", resourceName).GetRawText());
        return new SystemTemplate(
            RequiredString(root, "id", resourceName),
            RequiredString(root, "name", resourceName),
            category,
            RequiredString(root, "description", resourceName),
            [.. Required(root, "channels", resourceName).EnumerateArray().Select(channel => channel.GetString()!)],
            root.TryGetProperty("requirement", out var requirement) && requirement.ValueKind == JsonValueKind.String ? requirement.GetString() : null,
            [.. Required(root, "parameters", resourceName).EnumerateArray().Select(parameter => ReadParameter(parameter, resourceName))],
            [.. Required(root, "preview", resourceName).EnumerateArray().Select(title => title.GetString()!)],
            FlowJson.Serialize(flow),
            Required(root, "layout", resourceName).GetRawText())
        {
            BlockCount = flow.Nodes.Count,
        };
    }

    private static TemplateParameter ReadParameter(JsonElement parameter, string resourceName) => new(
        RequiredString(parameter, "key", resourceName),
        RequiredString(parameter, "label", resourceName),
        RequiredString(parameter, "kind", resourceName),
        parameter.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
            ? [.. options.EnumerateArray().Select(option => option.GetString()!)]
            : null,
        RequiredString(parameter, "defaultValue", resourceName));

    private static JsonElement Required(JsonElement element, string name, string resourceName) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new InvalidOperationException($"Falta «{name}» en la plantilla «{resourceName}».");

    private static string RequiredString(JsonElement element, string name, string resourceName) =>
        Required(element, name, resourceName) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw new InvalidOperationException($"«{name}» de la plantilla «{resourceName}» tiene que ser un texto.");
}
