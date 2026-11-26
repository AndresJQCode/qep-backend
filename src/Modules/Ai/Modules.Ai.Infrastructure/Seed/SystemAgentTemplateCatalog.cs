using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Infrastructure.Seed;

/// <summary>
/// Spec 2026-11-23 §2 y §3: las plantillas de agente del sistema son los <c>Seed/AgentTemplates/*.json</c> embebidos en este ensamblado,
/// un archivo por plantilla, calcadas de <c>SystemTemplateCatalog</c> de Automations. Se leen una vez, al construir el singleton, y
/// <c>AiDatabaseInitializer</c> lo construye al arrancar el host. Un archivo roto (o ningún archivo: el glob del csproj o el espacio de
/// nombres cambió) es un error de quien lo escribió, no del tenant: lanza <see cref="InvalidOperationException"/> y el host no arranca.
/// El <c>config</c> se lee con <see cref="JsonUnmappedMemberHandling.Disallow"/>: una clave mal escrita (<c>answerOnlyFromSource</c>) no se
/// puede tomar en silencio por su valor por defecto.
/// </summary>
internal sealed partial class SystemAgentTemplateCatalog : ISystemAgentTemplateCatalog
{
    private const string ResourceMarker = ".Seed.AgentTemplates.";

    // Sólo para validar: ConfigJson guarda el texto crudo y AgentConfig.FromJson lo lee con sus propias opciones, que toleran claves de más.
    private static readonly JsonSerializerOptions StrictConfig = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, SystemAgentTemplate> _byId;

    public SystemAgentTemplateCatalog()
        : this(typeof(SystemAgentTemplateCatalog).Assembly)
    {
    }

    /// <summary>Con otro ensamblado sólo para la prueba de que un catálogo sin recursos no arranca.</summary>
    internal SystemAgentTemplateCatalog(Assembly assembly)
        : this(ReadResources(assembly))
    {
    }

    /// <summary>Con archivos en memoria, para probar cada regla de arranque sin tocar el ensamblado.</summary>
    internal SystemAgentTemplateCatalog(IReadOnlyList<(string ResourceName, string Json)> files)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"No hay plantillas de agente del sistema (recursos *{ResourceMarker}*.json): revisa el EmbeddedResource del csproj.");
        }

        _byId = new Dictionary<string, SystemAgentTemplate>(StringComparer.Ordinal);
        var all = new List<SystemAgentTemplate>();
        foreach (var (resourceName, json) in files)
        {
            var template = Read(resourceName, json);
            if (!_byId.TryAdd(template.Id, template))
            {
                throw new InvalidOperationException($"Hay dos plantillas de agente con el id «{template.Id}».");
            }

            all.Add(template);
        }

        All = all;
    }

    public IReadOnlyList<SystemAgentTemplate> All { get; }

    public SystemAgentTemplate? Find(string id) => _byId.GetValueOrDefault(id);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Kebab();

    private static List<(string, string)> ReadResources(Assembly assembly)
    {
        // Por nombre de recurso: el orden no depende del sistema de archivos de quien compiló.
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        var files = new List<(string, string)>();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"No se pudo abrir el recurso embebido «{name}».");
            using var reader = new StreamReader(stream);
            files.Add((name, reader.ReadToEnd()));
        }

        return files;
    }

    private static SystemAgentTemplate Read(string resourceName, string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"La plantilla «{resourceName}» no se puede leer: {exception.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"La plantilla «{resourceName}» no se puede leer: el archivo tiene que ser un objeto.");
        }

        var id = RequiredString(root, "id", resourceName);
        var name = RequiredString(root, "name", resourceName);
        var description = RequiredString(root, "description", resourceName);
        var requires = Required(root, "requires", resourceName);
        var config = Required(root, "config", resourceName);

        var start = resourceName.LastIndexOf(ResourceMarker, StringComparison.Ordinal) + ResourceMarker.Length;
        var fileName = resourceName[start..^".json".Length];
        if (id.Length > SystemAgentTemplate.IdMaxLength || !Kebab().IsMatch(id) || !string.Equals(id, fileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"El id «{id}» de la plantilla «{resourceName}» tiene que ser kebab-case, de hasta {SystemAgentTemplate.IdMaxLength} caracteres, e igual al nombre del archivo.");
        }

        Bounded(name, "name", Agent.NameMaxLength, resourceName);
        Bounded(description, "description", SystemAgentTemplate.DescriptionMaxLength, resourceName);

        var keys = ReadModules(requires, resourceName);
        CheckConfig(config, resourceName);
        return new SystemAgentTemplate(id, name, description, keys, config.GetRawText());
    }

    private static void Bounded(string value, string field, int max, string resourceName)
    {
        if (value.Length is 0 || value.Length > max)
        {
            throw new InvalidOperationException($"«{field}» de la plantilla «{resourceName}» tiene que tener entre 1 y {max} caracteres.");
        }
    }

    private static List<TenantModuleKey> ReadModules(JsonElement requires, string resourceName)
    {
        if (requires.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"«requires» de la plantilla «{resourceName}» tiene que ser una lista.");
        }

        var keys = new List<TenantModuleKey>();
        foreach (var item in requires.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString();
            TenantModuleKey key;
            try
            {
                key = TenantModuleKey.Parse(text);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"La plantilla «{resourceName}» pide el módulo «{text}», que no existe.");
            }

            if (keys.Contains(key))
            {
                throw new InvalidOperationException($"La plantilla «{resourceName}» repite el módulo «{text}».");
            }

            keys.Add(key);
        }

        return keys;
    }

    private static void CheckConfig(JsonElement config, string resourceName)
    {
        if (config.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"El config de la plantilla «{resourceName}» tiene que ser un objeto.");
        }

        if (config.TryGetProperty("model", out _))
        {
            throw new InvalidOperationException(
                $"El config de la plantilla «{resourceName}» no puede traer «model»: el modelo lo elige el tenant.");
        }

        if (config.TryGetProperty("quotationDraft", out var draft) && draft.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"El config de la plantilla «{resourceName}» no puede traer «quotationDraft»: lleva datos del tenant.");
        }

        if (config.TryGetProperty("handoff", out var handoff) && handoff.ValueKind == JsonValueKind.Object
            && handoff.TryGetProperty("memberId", out var member) && member.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"El config de la plantilla «{resourceName}» no puede traer «handoff.memberId»: lleva datos del tenant.");
        }

        try
        {
            // Sin model, el record queda con Model nulo: ConfigFor lo completa. Lo que se valida acá es la forma.
            _ = JsonSerializer.Deserialize<AgentConfig>(config.GetRawText(), StrictConfig);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"La plantilla «{resourceName}» no se puede leer: {exception.Message}");
        }
    }

    private static JsonElement Required(JsonElement element, string name, string resourceName) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new InvalidOperationException($"Falta «{name}» en la plantilla «{resourceName}».");

    private static string RequiredString(JsonElement element, string name, string resourceName) =>
        Required(element, name, resourceName) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw new InvalidOperationException($"«{name}» de la plantilla «{resourceName}» tiene que ser un texto.");
}
