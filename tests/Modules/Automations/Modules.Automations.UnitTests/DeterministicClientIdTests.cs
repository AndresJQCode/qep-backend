using Modules.Automations.Application.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>Ruling P2-R5: un reintento de la fila deriva los mismos ids (la corrida nueva del mismo entrante y cada envío de la unidad),
/// así Mensajería reconoce lo que ya salió y no lo vuelve a mandar.</summary>
public sealed class DeterministicClientIdTests
{
    private static readonly Guid RunId = Guid.Parse("0199f0aa-0000-7000-8000-000000000001");

    [Fact]
    public void ClientIdIsStableForTheSameTuple()
    {
        Assert.Equal(DeterministicClientId.For(RunId, 4, 1), DeterministicClientId.For(RunId, 4, 1));
        Assert.NotEqual(DeterministicClientId.For(RunId, 4, 1), DeterministicClientId.For(RunId, 5, 1));
        Assert.NotEqual(DeterministicClientId.For(RunId, 4, 1), DeterministicClientId.For(RunId, 4, 2));
        Assert.NotEqual(DeterministicClientId.For(RunId, 4, 1), DeterministicClientId.For(Guid.CreateVersion7(), 4, 1));
        Assert.NotEqual(Guid.Empty, DeterministicClientId.For(RunId, 0, 0));
    }

    [Fact]
    public void RunIdIsStableForTheSameInboundAndDiffersFromItsClientIds()
    {
        var messageId = Guid.CreateVersion7();

        Assert.Equal(DeterministicClientId.RunIdFor(messageId), DeterministicClientId.RunIdFor(messageId));
        Assert.NotEqual(DeterministicClientId.RunIdFor(messageId), DeterministicClientId.RunIdFor(Guid.CreateVersion7()));
        Assert.NotEqual(messageId, DeterministicClientId.RunIdFor(messageId));
    }

    // «{a}:{b}:{c}» sin separar los números de otra forma haría que (1, 23) y (12, 3) choquen; con «:» no.
    [Fact]
    public void BaseAndIndexDoNotCollideByConcatenation() =>
        Assert.NotEqual(DeterministicClientId.For(RunId, 1, 23), DeterministicClientId.For(RunId, 12, 3));
}
