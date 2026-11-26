using System.Text.Json;

namespace Modules.Automations.Domain.Flow;

/// <summary>
/// Spec 2026-11-18: los ids de archivo (<see cref="MessageMedia.FileId"/>) que un flujo usa. El editor los resuelve en un solo lote
/// (<c>mediaFiles</c>), y lo que sigue en uso se pregunta sobre el jsonb guardado, sin pasar por el lector estricto.
/// </summary>
public static class FlowMedia
{
    /// <summary>Cada id una vez, en el orden de los bloques.</summary>
    public static IReadOnlyList<Guid> FileIdsOf(FlowDefinition flow) =>
        flow.Nodes
            .OfType<MessageNode>()
            .Select(message => message.Media?.FileId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

    /// <summary>
    /// Lectura tolerante del JSON crudo: <c>nodes[*]</c> con <c>kind</c> <c>message</c> y <c>media.fileId</c> que es un id. Lo demás
    /// (otro tipo de bloque, un id roto, un JSON que no es un flujo) se ignora; nunca lanza: un JSON ilegible da <c>[]</c>.
    /// </summary>
    public static IReadOnlyList<Guid> FileIdsIn(string flowJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(flowJson);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<Guid>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (FileIdOf(node) is { } fileId && !ids.Contains(fileId))
                {
                    ids.Add(fileId);
                }
            }

            return ids;
        }
    }

    private static Guid? FileIdOf(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty("kind", out var kind)
        && kind.ValueKind == JsonValueKind.String
        && kind.ValueEquals(FlowNodeKinds.Message)
        && node.TryGetProperty("media", out var media)
        && media.ValueKind == JsonValueKind.Object
        && media.TryGetProperty("fileId", out var fileId)
        && fileId.ValueKind == JsonValueKind.String
        && Guid.TryParse(fileId.GetString(), out var id)
            ? id
            : null;
}
