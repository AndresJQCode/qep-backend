using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Modules.Ai.Application;

namespace Modules.Ai.Infrastructure.Model.Anthropic;

/// <summary>
/// C-10: los bloques de una respuesta de Claude, guardados en su forma de petición y devueltos tal cual en la siguiente llamada del mismo
/// trabajo. El pensamiento sólo vale sin modificar (<c>signature</c> incluida), así que se copia campo por campo, nunca se rearma. Un
/// bloque que no es texto, pensamiento ni herramienta (por ejemplo, el aviso del respaldo de C-9) viaja con su JSON entero.
/// </summary>
internal static class OpaqueBlocks
{
    public static AiOpaqueBlock? FromResponse(BetaContentBlock block)
    {
        object? param = null;
        if (block.TryPickThinking(out var thinking))
        {
            param = new { type = "thinking", thinking = thinking.Thinking, signature = thinking.Signature };
        }
        else if (block.TryPickRedactedThinking(out var redacted))
        {
            param = new { type = "redacted_thinking", data = redacted.Data };
        }
        else if (block.TryPickText(out var text))
        {
            param = new { type = "text", text = text.Text };
        }
        else if (block.TryPickToolUse(out var toolUse))
        {
            param = new { type = "tool_use", id = toolUse.ID, name = toolUse.Name, input = toolUse.Input };
        }

        return param is null
            ? new AiOpaqueBlock(JsonSerializer.Serialize(block.Json))
            : new AiOpaqueBlock(JsonSerializer.Serialize(param));
    }

    public static BetaContentBlockParam ToParam(AiOpaqueBlock block)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(block.Json)
            ?? throw new InvalidOperationException("An opaque Anthropic block is empty.");
        var type = raw.TryGetValue("type", out var value) ? value.GetString() : null;
        return type switch
        {
            "thinking" => BetaThinkingBlockParam.FromRawUnchecked(raw),
            "redacted_thinking" => BetaRedactedThinkingBlockParam.FromRawUnchecked(raw),
            "text" => BetaTextBlockParam.FromRawUnchecked(raw),
            "tool_use" => BetaToolUseBlockParam.FromRawUnchecked(raw),
            _ => new BetaContentBlockParam(JsonSerializer.SerializeToElement(raw)),
        };
    }
}
