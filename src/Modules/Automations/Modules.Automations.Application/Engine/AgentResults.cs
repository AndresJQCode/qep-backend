using System.Text.Json;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-11-17 (agente IA en chatbots) §5.3 y §6.3: lee el <c>result</c> que Ai dejó en la delegación (<c>AgentBlockResult</c>:
/// <c>{output, reason, summary?, quotationNumber?, advisorMemberId?}</c>, camelCase) como la entrada del motor. Lo escribió otro módulo, así
/// que es tolerante: un campo ausente o de otro tipo se lee <c>null</c>, y un JSON que no se puede leer es una falla del agente (sale por
/// <c>error</c>) en vez de dejar la corrida esperando para siempre.
/// </summary>
public static class AgentResults
{
    /// <summary>El <c>reason</c> de un resultado que no se pudo leer.</summary>
    public const string Unreadable = "result_unreadable";

    public static EngineInput.AgentResult Parse(string nodeId, Guid delegationId, string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failed(nodeId, delegationId);
            }

            Guid? advisor = Text(root, "advisorMemberId") is { } text && Guid.TryParse(text, out var parsed) ? parsed : null;
            return new EngineInput.AgentResult(
                nodeId, delegationId, Text(root, "output"), Text(root, "reason"), Text(root, "summary"), Text(root, "quotationNumber"), advisor);
        }
        catch (JsonException)
        {
            return Failed(nodeId, delegationId);
        }
    }

    private static EngineInput.AgentResult Failed(string nodeId, Guid delegationId) =>
        new(nodeId, delegationId, FlowOutputs.Error, Unreadable);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
