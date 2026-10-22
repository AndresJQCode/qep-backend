using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-10-21 D-C6 y D-M16: qué entrante arranca una corrida en una conversación sin corrida y en <c>None</c>.</summary>
public sealed class TriggerEvaluatorTests
{
    private static readonly string[] Keywords = ["hola", "servicio tecnico"];

    [Fact]
    public void NewConversationAppliesOnlyWhenOpened()
    {
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.NewConversation, [], opened: true, "lo que sea", businessHoursOpen: true));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.NewConversation, [], opened: false, "lo que sea", businessHoursOpen: true));
    }

    [Fact]
    public void KeywordAppliesOnAnyInboundWithoutAccents()
    {
        // D-M16: con cualquier entrante, haya abierto o no la conversación; D-C6: palabra exacta sin mayúsculas ni tildes.
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, opened: false, "  Servicio TÉCNICO ", businessHoursOpen: true));
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, opened: true, "HOLA", businessHoursOpen: false));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, opened: true, "hola, necesito ayuda", businessHoursOpen: true));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.Keyword, Keywords, opened: true, null, businessHoursOpen: true));
    }

    [Fact]
    public void OutsideHoursNeedsOpenedAndClosedHours()
    {
        Assert.True(TriggerEvaluator.Applies(ChatbotTrigger.OutsideHours, [], opened: true, "hola", businessHoursOpen: false));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.OutsideHours, [], opened: true, "hola", businessHoursOpen: true));
        Assert.False(TriggerEvaluator.Applies(ChatbotTrigger.OutsideHours, [], opened: false, "hola", businessHoursOpen: false));
    }
}
