using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §5.1, §5.2, D20, D22, D35 y P13 del plan: cuándo una conversación está pendiente de triage y cuándo
/// un negocio está estancado. Funciones puras: las usan el tablero, el triage y el resumen del cliente.</summary>
public sealed class PipelineRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.Parse("01900000-0000-7000-8000-0000000000c1");

    [Fact]
    public void AnOpenConversationWithoutDealNorDecisionIsPending() =>
        Assert.True(TriageRules.IsPending(true, CustomerId, false, Now.AddHours(-1), null));

    [Fact]
    public void ACustomerWithAnOpenDealIsNotPending() =>
        Assert.False(TriageRules.IsPending(true, CustomerId, true, Now.AddHours(-1), null));

    [Fact]
    public void AResolvedConversationIsNotPending() =>
        Assert.False(TriageRules.IsPending(false, CustomerId, false, Now.AddHours(-1), null));

    // D35: una fila vieja sin cliente queda fuera del triage.
    [Fact]
    public void AConversationWithoutCustomerIsNotPending() =>
        Assert.False(TriageRules.IsPending(true, null, false, Now.AddHours(-1), null));

    // D20 y D38: la decisión vale contra el entrante con el que se tomó; un entrante nuevo es un instante nuevo.
    [Fact]
    public void ADismissalHidesItUntilTheCustomerWritesAgain()
    {
        var inbound = Now.AddDays(-30);

        Assert.False(TriageRules.IsPending(true, CustomerId, false, inbound, inbound));
        Assert.True(TriageRules.IsPending(true, CustomerId, false, Now, inbound));
    }

    // P13 y D38: sin entrante, la decisión se toma contra NoInbound.
    [Fact]
    public void WithoutInboundItIsPendingUntilDecided()
    {
        Assert.True(TriageRules.IsPending(true, CustomerId, false, null, null));
        Assert.False(TriageRules.IsPending(true, CustomerId, false, null, TriageDecision.NoInbound));
    }

    [Fact]
    public void AnOpenDealPastItsStallDaysIsStalled()
    {
        Assert.False(StallRules.IsStalled(StageKind.Open, 3, Now.AddDays(-3), Now));
        Assert.True(StallRules.IsStalled(StageKind.Open, 3, Now.AddDays(-3).AddSeconds(-1), Now));
    }

    [Fact]
    public void AClosingStageIsNeverStalled()
    {
        Assert.False(StallRules.IsStalled(StageKind.Won, null, Now.AddDays(-400), Now));
        Assert.False(StallRules.IsStalled(StageKind.Lost, 3, Now.AddDays(-400), Now));
    }

    // §5.2: última actividad = lo más reciente entre el cambio del negocio y el último mensaje (entrante o saliente).
    [Fact]
    public void LastActivityIsTheLatestOfDealChangeAndMessage()
    {
        Assert.Equal(Now, StallRules.LastActivity(Now.AddDays(-5), Now));
        Assert.Equal(Now, StallRules.LastActivity(Now, Now.AddDays(-5)));
        Assert.Equal(Now, StallRules.LastActivity(Now, null));
    }

    [Fact]
    public void DaysInFloorsAndNeverGoesNegative()
    {
        Assert.Equal(2, StallRules.DaysIn(Now.AddDays(-2).AddHours(-23), Now));
        Assert.Equal(0, StallRules.DaysIn(Now.AddMinutes(5), Now));
    }

    [Fact]
    public void ADismissalKeepsTheReasonAndTheActor()
    {
        var tenantId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var reasonId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();

        var decision = TriageDecision.Dismissed(tenantId, conversationId, CustomerId, reasonId, Now.AddHours(-2), DealActor.ForUser(memberId), Now);

        Assert.Equal((TriageDecisionKind.Dismissed, reasonId, (Guid?)null), (decision.Kind, decision.DismissReasonId, decision.DealId));
        Assert.Equal((ActorKind.User, (Guid?)memberId, Now), (decision.ActorKind, decision.ActorMemberId, decision.DecidedAt));
        Assert.Equal((tenantId, conversationId, CustomerId), (decision.TenantId, decision.ConversationId, decision.CustomerId));
        Assert.Equal(Now.AddHours(-2), decision.DecidedAgainstInboundAt);
    }

    // D38: sin entrante, la decisión guarda NoInbound (la columna del índice único no admite nulos).
    [Fact]
    public void ADecisionWithoutInboundIsTakenAgainstNoInbound()
    {
        var decision = TriageDecision.Created(Guid.CreateVersion7(), Guid.CreateVersion7(), CustomerId, Guid.CreateVersion7(), null, DealActor.ForSystem, Now);

        Assert.Equal(DateTimeOffset.UnixEpoch, TriageDecision.NoInbound);
        Assert.Equal(TriageDecision.NoInbound, decision.DecidedAgainstInboundAt);
    }
}
