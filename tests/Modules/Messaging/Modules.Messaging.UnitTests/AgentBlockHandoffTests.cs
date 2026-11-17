using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots), adenda D-6: el traspaso por una salida del bloque «Agente IA» sin conectar lleva la salida
/// como código y la foto de los nombres del chatbot y del agente.</summary>
public sealed class AgentBlockHandoffTests
{
    [Theory]
    [InlineData("human")]
    [InlineData("noAnswer")]
    [InlineData("error")]
    public void TheOutputIsTheCodeAndTheNamesSurvive(string output)
    {
        var handoff = AutomationHandoff.Create(
            AutomationHandoffKind.AgentBlockOutputUnhandled, output, "ai-agent-1", "Asesor IA", null, null, null,
            chatbotName: "  Bienvenida ", agentName: new string('a', 300));

        Assert.Equal(output, handoff.Code);
        Assert.Equal("Bienvenida", handoff.ChatbotName);
        Assert.Equal(AutomationHandoff.TextMaxLength, handoff.AgentName!.Length);
    }

    [Fact]
    public void AnUnknownOutputIsDropped() =>
        Assert.Null(AutomationHandoff.Create(AutomationHandoffKind.AgentBlockOutputUnhandled, "Done Now", null, null, null, null, null).Code);

    [Fact]
    public void TheNamesTravelInTheEvent()
    {
        var handoff = AutomationHandoff.Create(
            AutomationHandoffKind.AgentBlockOutputUnhandled, "noAnswer", "ai-agent-1", "Asesor IA", null, null, null,
            chatbotName: "Bienvenida", agentName: "Ventas");
        var written = ConversationEventJson.Serialize(new ConversationEvent(
            ConversationEventType.AutomationPaused, PauseReason: AutomationPauseReason.Handoff, Handoff: handoff));

        var read = ConversationEventJson.Parse(written)!.Handoff!;

        Assert.Equal(
            (AutomationHandoffKind.AgentBlockOutputUnhandled, "noAnswer", "Bienvenida", "Ventas"),
            (read.Kind, read.Code, read.ChatbotName, read.AgentName));
    }
}
