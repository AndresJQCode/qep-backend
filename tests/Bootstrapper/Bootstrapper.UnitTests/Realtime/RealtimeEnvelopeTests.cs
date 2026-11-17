using System.Text.Json;
using Bootstrapper.Realtime;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>Plan N8: the envelope that travels through <c>NOTIFY qep_realtime</c> is camelCase JSON, and whatever the listener cannot read
/// is dropped (null) instead of reaching the hub.</summary>
public sealed class RealtimeEnvelopeTests
{
    [Fact]
    public void TheEnvelopeRoundTripsInCamelCase()
    {
        var tenant = Guid.CreateVersion7();
        var ticket = Guid.CreateVersion7();
        var envelope = new RealtimeEnvelope(
            "supportTicketChanged", tenant, "support", JsonSerializer.SerializeToElement(new { ticketId = ticket }));

        var json = RealtimeEventJson.Serialize(envelope);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("supportTicketChanged", root.GetProperty("kind").GetString());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal("support", root.GetProperty("topic").GetString());
        Assert.Equal(ticket, root.GetProperty("payload").GetProperty("ticketId").GetGuid());

        var parsed = RealtimeEventJson.TryDeserialize(json);
        Assert.NotNull(parsed);
        Assert.Equal(envelope.Kind, parsed.Kind);
        Assert.Equal(envelope.TenantId, parsed.TenantId);
        Assert.Equal(envelope.Topic, parsed.Topic);
        Assert.Equal(ticket, parsed.Payload.GetProperty("ticketId").GetGuid());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"kind":"supportTicketChanged","tenantId":"00000000-0000-0000-0000-000000000000","topic":"support","payload":{}}""")]
    [InlineData("""{"kind":"","tenantId":"0190a000-0000-7000-8000-000000000001","topic":"support","payload":{}}""")]
    [InlineData("""{"kind":"supportTicketChanged","tenantId":"0190a000-0000-7000-8000-000000000001","topic":"","payload":{}}""")]
    [InlineData("null")]
    public void AnUnreadablePayloadIsNull(string payload) =>
        Assert.Null(RealtimeEventJson.TryDeserialize(payload));
}
