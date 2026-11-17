using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3: la delegación neutral de Mensajería nace <c>Live</c> y termina una sola vez,
/// devuelta con su resultado o interrumpida (<c>CK_bot_delegations_status</c>, <c>CK_bot_delegations_result</c>).</summary>
public sealed class BotDelegationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Brief = """{"objective":"Resuelve dudas","context":"","firstTurn":"lastMessage","idleMinutes":60}""";
    private const string Result = """{"output":"done","reason":"objective_completed","summary":"Listo"}""";

    private static BotDelegation Live(Guid? lastInbound = null) => BotDelegation.Start(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), "ai-agent-1", Guid.CreateVersion7(), lastInbound, Brief, Now);

    [Fact]
    public void ADelegationStartsLiveWithItsBriefAndWithoutResult()
    {
        var inbound = Guid.CreateVersion7();

        var delegation = Live(inbound);

        Assert.Equal(BotDelegationStatus.Live, delegation.Status);
        Assert.Equal(Brief, delegation.Brief);
        Assert.Equal("ai-agent-1", delegation.NodeId);
        Assert.Equal(inbound, delegation.LastInboundMessageId);
        Assert.Null(delegation.Result);
        Assert.Equal(Now, delegation.StartedAt);
        Assert.Null(delegation.EndedAt);
    }

    [Fact]
    public void ReturningRecordsTheResultAndTheEnd()
    {
        var delegation = Live();

        delegation.Return(Result, Now.AddMinutes(3));

        Assert.Equal(BotDelegationStatus.Returned, delegation.Status);
        Assert.Equal(Result, delegation.Result);
        Assert.Equal(Now.AddMinutes(3), delegation.EndedAt);
    }

    [Theory]
    [InlineData(BotDelegationStatus.Returned)]
    [InlineData(BotDelegationStatus.Interrupted)]
    public void ReturningIsOnlyFromLive(BotDelegationStatus ended)
    {
        var delegation = Live();
        if (ended == BotDelegationStatus.Returned)
        {
            delegation.Return(Result, Now.AddMinutes(1));
        }
        else
        {
            delegation.Interrupt(Now.AddMinutes(1));
        }

        var error = Assert.Throws<MessagingDomainException>(() => delegation.Return(Result, Now.AddMinutes(2)));

        Assert.Equal(MessagingErrorCodes.DelegationNotLive, error.Code);
        Assert.Equal(ended, delegation.Status);
        Assert.Equal(Now.AddMinutes(1), delegation.EndedAt);
    }

    [Fact]
    public void InterruptingALiveDelegationEndsItWithoutResult()
    {
        var delegation = Live();

        var interrupted = delegation.Interrupt(Now.AddMinutes(2));

        Assert.True(interrupted);
        Assert.Equal(BotDelegationStatus.Interrupted, delegation.Status);
        Assert.Null(delegation.Result);
        Assert.Equal(Now.AddMinutes(2), delegation.EndedAt);
    }

    [Fact]
    public void InterruptingAnEndedDelegationChangesNothing()
    {
        var delegation = Live();
        delegation.Return(Result, Now.AddMinutes(1));

        var interrupted = delegation.Interrupt(Now.AddMinutes(2));

        // Una señal de toma que llega tarde no pisa un retorno que ya ocurrió.
        Assert.False(interrupted);
        Assert.Equal(BotDelegationStatus.Returned, delegation.Status);
        Assert.Equal(Now.AddMinutes(1), delegation.EndedAt);
    }

    [Theory]
    [InlineData("", Brief)]
    [InlineData("   ", Brief)]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", Brief)]
    [InlineData("ai-agent-1", "")]
    [InlineData("ai-agent-1", " ")]
    public void AStartWithoutNodeOrBriefIsAProgrammingError(string nodeId, string brief) =>
        Assert.ThrowsAny<ArgumentException>(() => BotDelegation.Start(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), nodeId, Guid.CreateVersion7(), null, brief, Now));

    [Fact]
    public void AnEmptyResultIsAProgrammingError() =>
        Assert.ThrowsAny<ArgumentException>(() => Live().Return(" ", Now));

    [Fact]
    public void TheStatusNamesAreTheStoredOnes() =>
        Assert.Equal(["Live", "Returned", "Interrupted"], Enum.GetNames<BotDelegationStatus>());
}
