using Modules.Messaging.Domain;
using AgentBlockOutputs = Modules.Ai.Application.AgentBlockOutputs;
using AgentBlockReasons = Modules.Ai.Application.AgentBlockReasons;
using AgentHandoffCodes = Modules.Ai.Application.AgentHandoffCodes;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-17 D-B10 y §6.3: Ai no referencia Messaging, así que las cinco salidas del bloque «Agente IA» viven dos veces
/// (<see cref="AgentBlockOutputs"/> y <see cref="BotDelegationOutputs"/>). Una salida que Ai escribe y Mensajería no reconoce hace fallar el
/// retorno (<c>Conversation.ReturnFromAgent</c> la rechaza). Y cada <c>reason</c> cumple la regla de códigos del hilo.
/// </summary>
public sealed class AgentBlockOutputsParityTests
{
    [Fact]
    public void TheOutputsAreTheSameInAiAndMessagingAndInTheSameOrder() =>
        Assert.Equal(BotDelegationOutputs.All, AgentBlockOutputs.All);

    [Fact]
    public void EveryBlockReasonFollowsTheThreadCodeRule() =>
        Assert.All(AgentBlockReasons.All.Concat(AgentHandoffCodes.All), reason => Assert.True(AutomationHandoff.IsValidCode(reason), reason));
}
