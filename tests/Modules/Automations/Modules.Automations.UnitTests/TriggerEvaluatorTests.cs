using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-21 D-C6 y spec 2026-10-26 D-1/D-1b: qué entrante arranca una corrida en una conversación sin corrida y sin asesor.</summary>
public sealed class TriggerEvaluatorTests
{
    private static readonly string[] Keywords = ["hola", "servicio tecnico"];

    [Fact]
    public void NewConversationAppliesOnAnyInbound()
    {
        // Reemplaza a D-M16: ya no depende de que el entrante haya abierto la conversación.
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.NewConversation, [], "lo que sea", businessHoursOpen: true));
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.NewConversation, [], null, businessHoursOpen: false));
    }

    [Fact]
    public void KeywordAppliesOnAnyInboundWithoutAccents()
    {
        // D-C6: palabra exacta sin mayúsculas ni tildes.
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, "  Servicio TÉCNICO ", businessHoursOpen: true));
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, "HOLA", businessHoursOpen: false));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, "hola, necesito ayuda", businessHoursOpen: true));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, null, businessHoursOpen: true));
    }

    [Fact]
    public void OutsideHoursAppliesOnlyWhileClosed()
    {
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.OutsideHours, [], "hola", businessHoursOpen: false));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.OutsideHours, [], "hola", businessHoursOpen: true));
    }
}
