using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-10 §6.1.5: el <c>details</c> de un evento lleva el tipo por nombre y sólo ids, sin las
/// claves que no aplican; lo que no se entiende se lee como «no es un evento», nunca como un 500.</summary>
public sealed class ConversationEventJsonTests
{
    private static readonly Guid Actor = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid Target = Guid.Parse("01900000-0000-7000-8000-0000000000a2");

    [Fact]
    public void OnlyTheKeysThatApplyAreWritten() =>
        Assert.Equal(
            $$"""{"type":"Transferred","actor":"{{Actor}}","target":"{{Target}}"}""",
            ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Transferred, Actor: Actor, Target: Target)));

    [Fact]
    public void EveryFieldRoundTrips()
    {
        var value = new ConversationEvent(ConversationEventType.ContactChangedNumber, Actor, Target, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        Assert.Equal(value, ConversationEventJson.Parse(ConversationEventJson.Serialize(value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"type":"Exploded"}""")]
    [InlineData("""{"actor":"01900000-0000-7000-8000-0000000000a1"}""")]
    public void WhatIsNotAnEventParsesAsNull(string? json) =>
        Assert.Null(ConversationEventJson.Parse(json));

    [Fact]
    public void ABadIdIsDroppedNotFatal() =>
        Assert.Equal(new ConversationEvent(ConversationEventType.Released), ConversationEventJson.Parse("""{"type":"Released","actor":"nope"}"""));
}
