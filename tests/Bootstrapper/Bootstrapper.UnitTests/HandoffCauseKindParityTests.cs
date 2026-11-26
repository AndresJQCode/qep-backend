using Modules.Messaging.Domain;
using AgentHandoffCodes = Modules.Ai.Application.AgentHandoffCodes;
using AiHandoffCauseKind = Modules.Ai.Application.HandoffCauseKind;
using EngineHandoffCauseKind = Modules.Automations.Application.Engine.HandoffCauseKind;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-10-24 §5.3 y spec 2026-10-29 §4.4: ni Automations ni Ai referencian Messaging, así que la causa de un traspaso tiene tres enums
/// (el del motor de chatbots, el de los agentes y <see cref="AutomationHandoffKind"/> del hilo) y los adaptadores traducen por nombre. Este
/// proyecto ve los tres módulos: si uno cambia sin el otro, esta prueba se rompe antes que el <c>Enum.Parse</c> del adaptador en producción.
/// </summary>
public sealed class HandoffCauseKindParityTests
{
    [Fact]
    public void EveryCauseKindExistsInMessaging() =>
        Assert.Equal(
            Enum.GetNames<EngineHandoffCauseKind>().Concat(Enum.GetNames<AiHandoffCauseKind>()).Order(StringComparer.Ordinal),
            Enum.GetNames<AutomationHandoffKind>().Order(StringComparer.Ordinal));

    [Fact]
    public void TheChatbotAndAgentKindsDoNotOverlap() =>
        Assert.Empty(Enum.GetNames<EngineHandoffCauseKind>().Intersect(Enum.GetNames<AiHandoffCauseKind>(), StringComparer.Ordinal));

    /// <summary>Plan 2026-11-12: los códigos nuevos (<c>ai.provider.permission_denied</c>, <c>ai.provider.refused</c>,
    /// <c>ai.agent.request_invalid</c>) y todos los demás de un agente sobreviven a la limpieza del hilo; uno que no, se guardaría
    /// <c>null</c> sin error.</summary>
    [Fact]
    public void EveryAgentHandoffCodeSurvivesTheThreadCleaning()
    {
        Assert.Contains("ai.provider.permission_denied", AgentHandoffCodes.All);
        Assert.Contains("ai.provider.refused", AgentHandoffCodes.All);
        Assert.Contains("ai.agent.request_invalid", AgentHandoffCodes.All);
        Assert.Contains("appointment_change_blocked", AgentHandoffCodes.All);
        Assert.Contains("appointment_customer_unavailable", AgentHandoffCodes.All);
        Assert.All(AgentHandoffCodes.All, code => Assert.True(AutomationHandoff.IsValidCode(code), code));
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    public void EveryProviderSurvivesTheThreadCleaning(string provider) =>
        Assert.Equal(
            provider,
            AutomationHandoff.Create(AutomationHandoffKind.ProviderFailed, null, null, null, null, null, null, provider: provider).Provider);
}
