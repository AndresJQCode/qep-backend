using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Domain.Engine;

/// <summary>
/// Las variables de una corrida (spec 2026-10-21 D-M8): un objeto JSON cuyas llaves son las variables raíz. Es lo que el motor guarda
/// en la corrida entre mensajes (<see cref="ToJson"/>/<see cref="Parse"/>) y lo que lee al interpolar y al evaluar condiciones.
/// </summary>
public sealed class RunVariables
{
    // Sin escapar tildes ni «ñ»: el JSON compacto de una variable se manda tal cual en un mensaje o en el cuerpo de una API.
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly JsonObject root;

    private RunVariables(JsonObject root) => this.root = root;

    public static RunVariables Empty() => new([]);

    /// <summary>Lee lo que escribió <see cref="ToJson"/>; vacío es «sin variables». Algo que no es un objeto lanza <see cref="JsonException"/>.</summary>
    public static RunVariables Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty();
        }

        return JsonNode.Parse(json) is JsonObject obj ? new RunVariables(obj) : throw new JsonException("Las variables de la corrida tienen que ser un objeto JSON.");
    }

    public string ToJson() => root.ToJsonString(Compact);

    public JsonNode? Get(string path) => JsonPath.Select(root, path);

    /// <summary>Reemplaza el valor entero de la variable raíz; no escribe sub-rutas (<c>cliente.id</c> no se asigna).</summary>
    public void Set(string name, JsonNode? value)
    {
        // Un JsonNode sólo puede tener un padre: se copia para que el mismo valor pueda guardarse en dos variables.
        root[name] = value?.DeepClone();
    }

    public bool Has(string path) => Get(path) is not null;

    /// <summary>El valor como texto para un mensaje: escalar tal cual (números y booleanos en cultura invariante), objeto o lista como
    /// JSON compacto, y <c>null</c> o ausente como vacío.</summary>
    public string TextOf(string path)
    {
        var node = Get(path);
        return node switch
        {
            null => string.Empty,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            JsonValue value when value.TryGetValue<decimal>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => node.ToJsonString(Compact),
        };
    }

    public IReadOnlyDictionary<string, JsonNode?> Snapshot() =>
        root.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone(), StringComparer.Ordinal);

    public RunVariables Clone() => new((JsonObject)root.DeepClone());
}
