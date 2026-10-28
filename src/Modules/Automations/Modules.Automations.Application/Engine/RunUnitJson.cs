using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Automations.Domain.Engine;

namespace Modules.Automations.Application.Engine;

public sealed record RunUnitWriteRow(string Name, JsonNode? Value, bool Truncated, int? Bytes);

public sealed record RunUnitStepRow(
    int Step, int Seq, string NodeId, string Kind, string Outcome, string? From, string? Via, IReadOnlyList<RunUnitWriteRow> Writes,
    IReadOnlyList<RunStepSend> Sends, Guid? InputMessageId, bool Reset);

/// <summary>
/// Spec 2026-10-25 D-E8: el JSON de la columna <c>steps</c> de <c>run_units</c>. Un valor cuyo JSON compacto pasa de
/// <see cref="ValueMaxBytes"/> se guarda como <c>{ "truncated": true, "bytes": N }</c>; si la fila pasa de <see cref="RowMaxBytes"/> se
/// descartan valores de a uno, desde el más grande. La URL firmada de un adjunto nunca se guarda.
/// </summary>
public static class RunUnitJson
{
    public const int ValueMaxBytes = 8 * 1024;
    public const int RowMaxBytes = 256 * 1024;

    // El mismo encoder mide y guarda: con el predeterminado una á pesa 6 bytes en el JSON y 2 en UTF-8, y el "bytes: N" de un valor
    // truncado no sería el tamaño real. jsonb de Postgres acepta la forma sin escapar.
    private static readonly JsonSerializerOptions StoredJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(IReadOnlyList<RunUnitStep> steps, IReadOnlySet<string> attachmentVariables)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(attachmentVariables);

        var rows = steps.Select(step => new Row(step, step.Record.Writes.Select(w => new WriteCell(w.Name, Sanitize(w, attachmentVariables))).ToList())).ToList();
        var json = ToJson(rows);

        // Se trunca de a un valor, el más grande que aún lo conserve, midiendo el JSON entero cada vez (D-E8).
        foreach (var cell in rows.SelectMany(row => row.Writes).Where(cell => !cell.Truncated).OrderByDescending(cell => cell.Size))
        {
            if (Encoding.UTF8.GetByteCount(json) <= RowMaxBytes)
            {
                break;
            }

            cell.Drop();
            json = ToJson(rows);
        }

        return json;
    }

    public static IReadOnlyList<RunUnitStepRow> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonNode.Parse(json)!.AsArray().Select(node => Read(node!.AsObject())).ToArray();
    }

    private static RunUnitStepRow Read(JsonObject node)
    {
        var writes = node["writes"]!.AsArray().Select(w =>
        {
            var write = w!.AsObject();
            return new RunUnitWriteRow(
                write["name"]!.GetValue<string>(), write["value"]?.DeepClone(), write["truncated"]?.GetValue<bool>() ?? false,
                write["bytes"]?.GetValue<int>());
        }).ToArray();
        var sends = node["sends"]!.AsArray().Select(s =>
        {
            var send = s!.AsObject();
            return new RunStepSend(
                send["kind"]!.GetValue<string>(),
                send["clientId"] is { } client ? Guid.Parse(client.GetValue<string>()) : null,
                send["transcriptIndex"]?.GetValue<int>());
        }).ToArray();
        return new RunUnitStepRow(
            node["step"]!.GetValue<int>(), node["seq"]!.GetValue<int>(), node["nodeId"]!.GetValue<string>(), node["kind"]!.GetValue<string>(),
            node["outcome"]!.GetValue<string>(), node["from"]?.GetValue<string>(), node["via"]?.GetValue<string>(), writes, sends,
            node["input"]?["messageId"] is { } input ? Guid.Parse(input.GetValue<string>()) : null, node["reset"]?.GetValue<bool>() ?? false);
    }

    private static string ToJson(List<Row> rows) => new JsonArray(rows.Select(row => (JsonNode?)row.ToNode()).ToArray()).ToJsonString(StoredJson);

    // Copia: el valor es el de las variables vivas de la corrida y no se muta.
    private static JsonNode? Sanitize(StepWrite write, IReadOnlySet<string> attachmentVariables)
    {
        var value = write.Value?.DeepClone();
        if (value is JsonObject saved && attachmentVariables.Contains(write.Name))
        {
            saved.Remove("url");
        }

        return value;
    }

    private sealed class WriteCell
    {
        private readonly string _name;
        private JsonNode? _value;
        private int? _truncatedBytes;

        public WriteCell(string name, JsonNode? value)
        {
            _name = name;
            _value = value;
            Size = Encoding.UTF8.GetByteCount(value?.ToJsonString(StoredJson) ?? "null");
            if (Size > ValueMaxBytes)
            {
                Drop();
            }
        }

        public int Size { get; }

        public bool Truncated => _truncatedBytes is not null;

        public void Drop()
        {
            _value = null;
            _truncatedBytes = Size;
        }

        public JsonObject ToNode()
        {
            var node = new JsonObject { ["name"] = _name };
            if (_truncatedBytes is { } bytes)
            {
                node["truncated"] = true;
                node["bytes"] = bytes;
            }
            else
            {
                node["value"] = _value?.DeepClone();
            }

            return node;
        }
    }

    private sealed class Row(RunUnitStep step, List<WriteCell> writes)
    {
        public List<WriteCell> Writes { get; } = writes;

        public JsonObject ToNode()
        {
            var record = step.Record;
            var node = new JsonObject
            {
                ["step"] = record.Step, ["seq"] = record.Seq, ["nodeId"] = record.NodeId, ["kind"] = record.Kind, ["outcome"] = record.Outcome,
            };
            if (record.FromNodeId is not null)
            {
                node["from"] = record.FromNodeId;
            }

            if (record.FromOutput is not null)
            {
                node["via"] = record.FromOutput;
            }

            node["writes"] = new JsonArray(Writes.Select(w => (JsonNode?)w.ToNode()).ToArray());
            node["sends"] = new JsonArray(step.Sends.Select(send =>
            {
                var s = new JsonObject { ["kind"] = send.Kind };
                if (send.ClientId is { } client)
                {
                    s["clientId"] = client.ToString();
                }

                if (send.TranscriptIndex is { } index)
                {
                    s["transcriptIndex"] = index;
                }

                return (JsonNode?)s;
            }).ToArray());
            if (step.InputMessageId is { } input)
            {
                node["input"] = new JsonObject { ["messageId"] = input.ToString() };
            }

            node["reset"] = record.Reset;
            return node;
        }
    }
}
