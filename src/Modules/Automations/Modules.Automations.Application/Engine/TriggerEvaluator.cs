using Modules.Automations.Domain;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-21 D-C6 y spec 2026-10-26 D-1/D-1b (que reemplazan D-M16): si el entrante arranca una corrida en una conversación sin
/// corrida y sin asesor. <c>NewConversation</c> con cualquier entrante, haya abierto o no la conversación; <c>OutsideHours</c> con
/// cualquiera mientras la conexión esté fuera de horario; <c>Keyword</c> con la palabra. Que la última corrida haya terminado sola (D-12)
/// no lo decide el disparador: lo mira el procesador.
/// </summary>
public static class TriggerEvaluator
{
    /// <param name="keywords">Las del chatbot, ya normalizadas por el agregado; se comparan con la palabra exacta del cliente, sin
    /// mayúsculas, tildes ni espacios de más (<see cref="TextMatch.IsAnyOf"/>).</param>
    /// <param name="businessHoursOpen">Sólo cuenta para <c>OutsideHours</c>: quien llama puede no consultarlo en los demás.</param>
    public static bool Applies(ChatbotTrigger trigger, IReadOnlyList<string> keywords, string? text, bool businessHoursOpen)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        return trigger switch
        {
            ChatbotTrigger.NewConversation => true,
            // Sin texto (un audio, un sticker) no hay palabra que comparar; un texto en blanco tampoco puede coincidir con una palabra.
            ChatbotTrigger.Keyword => !string.IsNullOrWhiteSpace(text) && TextMatch.IsAnyOf(text, keywords),
            ChatbotTrigger.OutsideHours => !businessHoursOpen,
            _ => false,
        };
    }
}
