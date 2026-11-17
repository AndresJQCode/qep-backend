namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-29 §4.3 y §4.4: qué tipo de bot atiende una conexión o una conversación. Viaja como smallint con estos valores en
/// <c>connection_bots.bot_kind</c>, <c>conversations.automation_bot_kind</c> y <c>automation_inbox.bot_kind</c>: no se reordenan.
/// </summary>
public enum BotKind
{
    Chatbot = 1,
    Agent = 2,
}

/// <summary>Spec 2026-10-29 §4.4: el bot que arranca o firma algo en Mensajería. Mensajería no conoce chatbots ni agentes: sólo su tipo y su
/// id, que son referencias blandas a Automations y a Ai.</summary>
public sealed record BotRef(BotKind Kind, Guid Id)
{
    public static BotRef Chatbot(Guid chatbotId) => new(BotKind.Chatbot, chatbotId);

    public static BotRef Agent(Guid agentId) => new(BotKind.Agent, agentId);
}
