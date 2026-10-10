using System.Text.Json;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Lo que se puede registrar de un error de Graph (spec §3, «Errores de Graph»): código,
/// subcódigo y <c>fbtrace_id</c>. Nunca <c>message</c>: puede repetir el token.</summary>
internal sealed record MetaGraphError(int? Code, int? Subcode, string? FbTraceId)
{
    public static MetaGraphError? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new MetaGraphError(
                error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var parsedCode) ? parsedCode : null,
                error.TryGetProperty("error_subcode", out var subcode) && subcode.ValueKind == JsonValueKind.Number && subcode.TryGetInt32(out var parsedSubcode) ? parsedSubcode : null,
                error.TryGetProperty("fbtrace_id", out var trace) && trace.ValueKind == JsonValueKind.String ? trace.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
