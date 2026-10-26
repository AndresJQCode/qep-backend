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

    private static readonly Guid Reason = Guid.Parse("01900000-0000-7000-8000-0000000000a3");

    // Spec 2026-10-15 §3.2: el motivo viaja como reasonId, y sólo cuando lo hay.
    [Fact]
    public void TheReasonIsWrittenAsReasonId() =>
        Assert.Equal(
            $$"""{"type":"Resolved","actor":"{{Actor}}","reasonId":"{{Reason}}"}""",
            ConversationEventJson.Serialize(new ConversationEvent(ConversationEventType.Resolved, Actor: Actor, ReasonId: Reason)));

    [Fact]
    public void TheReasonRoundTrips()
    {
        var value = new ConversationEvent(ConversationEventType.Resolved, Actor: Actor, ReasonId: Reason);

        Assert.Equal(value, ConversationEventJson.Parse(ConversationEventJson.Serialize(value)));
    }

    // Los eventos escritos antes de esta spec no tienen la clave: se leen sin motivo.
    [Fact]
    public void AnOldResolvedEventReadsWithoutAReason() =>
        Assert.Equal(
            new ConversationEvent(ConversationEventType.Resolved, Actor: Actor),
            ConversationEventJson.Parse($$"""{"type":"Resolved","actor":"{{Actor}}"}"""));

    [Fact]
    public void ThePauseReasonRoundTripsByName()
    {
        var value = new ConversationEvent(ConversationEventType.AutomationPaused, Actor: Actor, PauseReason: AutomationPauseReason.AdvisorReplied);

        var json = ConversationEventJson.Serialize(value);

        Assert.Contains("\"pauseReason\":\"AdvisorReplied\"", json);
        Assert.Equal(value, ConversationEventJson.Parse(json));
    }

    [Fact]
    public void AnUnknownPauseReasonReadsAsNull() =>
        Assert.Equal(
            new ConversationEvent(ConversationEventType.AutomationPaused),
            ConversationEventJson.Parse("""{"type":"AutomationPaused","pauseReason":"Whatever"}"""));

    // Spec 2026-10-24 §5.2: el caso 1 del owner.
    private static readonly ConversationEvent HandoffEvent = new(
        ConversationEventType.AutomationPaused,
        PauseReason: AutomationPauseReason.Handoff,
        Handoff: AutomationHandoff.Create(
            AutomationHandoffKind.SendFailed, "messaging.connection_unavailable", "handoff", "Pasar a un asesor",
            "Servicio Clasic", "secret_unreadable",
            new AutomationHandoffHttp(
                "policy-lookup", "Buscar cliente por teléfono", "connection_unavailable", "secret_unreadable", null, "API de Clasic")));

    [Fact]
    public void AHandoffRoundTrips() =>
        Assert.Equal(HandoffEvent, ConversationEventJson.Parse(ConversationEventJson.Serialize(HandoffEvent)));

    // Spec 2026-10-25 D-8: a quién quedó asignada la conversación al agotarse los intentos.
    private static ConversationEvent AssignedEvent(Guid? member) => new(
        ConversationEventType.AutomationPaused,
        PauseReason: AutomationPauseReason.Handoff,
        Handoff: AutomationHandoff.Create(
            AutomationHandoffKind.AttemptsExhausted, null, "ask", "Número de documento", null, null, null, member));

    [Fact]
    public void AnAssignedHandoffRoundTrips()
    {
        var value = AssignedEvent(Guid.CreateVersion7());

        Assert.Equal(value, ConversationEventJson.Parse(ConversationEventJson.Serialize(value)));
    }

    [Fact]
    public void TheAssignedMemberIsWrittenAsAssignedMemberId()
    {
        var member = Guid.CreateVersion7();

        var assigned = ConversationEventJson.Serialize(AssignedEvent(member));
        var unassigned = ConversationEventJson.Serialize(AssignedEvent(null));

        Assert.Contains("\"kind\":\"AttemptsExhausted\"", assigned, StringComparison.Ordinal);
        Assert.Contains($"\"assignedMemberId\":\"{member:D}\"", assigned, StringComparison.Ordinal);
        Assert.DoesNotContain("assignedMemberId", unassigned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"ana\"")]
    [InlineData("12")]
    public void AnUnreadableAssignedMemberReadsAsNull(string value)
    {
        var parsed = ConversationEventJson.Parse(
            $$$"""{"type":"AutomationPaused","pauseReason":"Handoff","handoff":{"kind":"AttemptsExhausted","nodeId":"ask","assignedMemberId":{{{value}}}}}""");

        Assert.Null(parsed!.Handoff!.AssignedMemberId);
        Assert.Equal("ask", parsed.Handoff.NodeId);
        Assert.Equal(AutomationHandoffKind.AttemptsExhausted, parsed.Handoff.Kind);
    }

    [Fact]
    public void TheHandoffIsWrittenWithItsNames()
    {
        var json = ConversationEventJson.Serialize(HandoffEvent);

        Assert.Contains(
            "\"handoff\":{\"kind\":\"SendFailed\",\"code\":\"messaging.connection_unavailable\",\"nodeId\":\"handoff\","
            + "\"nodeTitle\":\"Pasar a un asesor\",\"connectionName\":\"Servicio Clasic\",\"connectionReason\":\"secret_unreadable\","
            + "\"http\":{\"nodeId\":\"policy-lookup\",",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"connection_unavailable\",\"errorCode\":\"secret_unreadable\",\"connectionName\":\"API de Clasic\"}}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventWithoutHandoffReadsAsBefore()
    {
        var parsed = ConversationEventJson.Parse("""{"type":"AutomationPaused","pauseReason":"Handoff"}""");

        Assert.NotNull(parsed);
        Assert.Null(parsed.Handoff);
        Assert.Equal(AutomationPauseReason.Handoff, parsed.PauseReason);
        Assert.DoesNotContain("handoff", ConversationEventJson.Serialize(parsed), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"AutomationPaused","pauseReason":"Handoff","handoff":{"kind":"Future"}}""")]
    [InlineData("""{"type":"AutomationPaused","pauseReason":"Handoff","handoff":"x"}""")]
    [InlineData("""{"type":"AutomationPaused","pauseReason":"Handoff","handoff":{"kind":7}}""")]
    [InlineData("""{"type":"AutomationPaused","pauseReason":"Handoff","handoff":{}}""")]
    public void AnUnknownKindOrAMalformedHandoffReadsAsNull(string json)
    {
        var parsed = ConversationEventJson.Parse(json);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Handoff);
        Assert.Equal(AutomationPauseReason.Handoff, parsed.PauseReason);
    }

    [Fact]
    public void AFieldOfAnotherTypeReadsAsNullAndTheRestIsRead()
    {
        var parsed = ConversationEventJson.Parse(
            """{"type":"AutomationPaused","pauseReason":"Handoff","handoff":{"kind":"Block","nodeId":"handoff","code":5,"http":{"nodeId":"policy-lookup","outcome":"error_status","status":"404","errorCode":"http_404"}}}""");

        Assert.Equal(
            AutomationHandoff.Create(
                AutomationHandoffKind.Block, null, "handoff", null, null, null,
                new AutomationHandoffHttp("policy-lookup", null, "error_status", "http_404", null, null)),
            parsed!.Handoff);
    }

    [Fact]
    public void WhatIsReadIsCleanedLikeWhatIsWritten()
    {
        var parsed = ConversationEventJson.Parse(
            """{"type":"AutomationPaused","pauseReason":"Handoff","handoff":{"kind":"SendFailed","code":"Ya existe una orden","http":{"nodeId":"policy-lookup","outcome":"error_status","status":700}}}""");

        Assert.Null(parsed!.Handoff!.Code);
        Assert.Null(parsed.Handoff.Http!.Status);
    }

    [Fact]
    public void CustomerChangedRoundTripsWithBothCustomers()
    {
        var value = new ConversationEvent(ConversationEventType.CustomerChanged, Actor, CustomerId: Guid.CreateVersion7(), PreviousCustomerId: Guid.CreateVersion7());

        var json = ConversationEventJson.Serialize(value);

        Assert.Contains("\"previousCustomerId\"", json);
        Assert.Equal(value, ConversationEventJson.Parse(json));
    }
}
