using Modules.Automations.Domain;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Application.Engine;

/// <summary>
/// Spec 2026-10-21 D-C6 y D-M16: si el entrante arranca una corrida en una conversación sin corrida y en <c>None</c>.
/// <c>NewConversation</c> y <c>OutsideHours</c> sólo con el entrante que abrió o reabrió la conversación (<paramref name="opened"/>), para
/// que el bot no salude a mitad de un hilo que un asesor liberó; <c>Keyword</c> con cualquiera.
/// </summary>
public static class TriggerEvaluator
{
    /// <param name="keywords">Las del chatbot, ya normalizadas por el agregado; se comparan con la palabra exacta del cliente, sin
    /// mayúsculas, tildes ni espacios de más (<see cref="TextMatch.IsAnyOf"/>).</param>
    /// <param name="businessHoursOpen">Sólo cuenta para <c>OutsideHours</c>: quien llama puede no consultarlo en los demás.</param>
    public static bool Applies(ChatbotTrigger trigger, IReadOnlyList<string> keywords, bool opened, string? text, bool businessHoursOpen)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        return trigger switch
        {
            ChatbotTrigger.NewConversation => opened,
            // Sin texto (un audio, un sticker) no hay palabra que comparar; un texto en blanco tampoco puede coincidir con una palabra.
            ChatbotTrigger.Keyword => !string.IsNullOrWhiteSpace(text) && TextMatch.IsAnyOf(text, keywords),
            ChatbotTrigger.OutsideHours => opened && !businessHoursOpen,
            _ => false,
        };
    }
}
