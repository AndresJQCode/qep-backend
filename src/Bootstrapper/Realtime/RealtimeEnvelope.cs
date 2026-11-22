using System.Text.Json;

namespace Bootstrapper.Realtime;

/// <summary>Plan N8: what travels through <c>NOTIFY qep_realtime</c>. <see cref="Payload"/> is what the browser receives as is.
/// <see cref="MemberId"/> (spec 2026-11-22) is set only for a notice to one member; a pod from before it reads the envelope without
/// it and sends to a group nobody is in.</summary>
internal sealed record RealtimeEnvelope(string Kind, Guid TenantId, string Topic, JsonElement Payload, Guid? MemberId = null);

/// <summary>camelCase (<c>JsonSerializerDefaults.Web</c>) like <c>MessagingEventJson</c>; what the listener cannot read is dropped
/// without bringing anything down.</summary>
internal static class RealtimeEventJson
{
    public const string Channel = "qep_realtime";

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(RealtimeEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static RealtimeEnvelope? TryDeserialize(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RealtimeEnvelope>(payload, Options);
            return parsed is { Kind.Length: > 0, Topic.Length: > 0 } && parsed.TenantId != Guid.Empty ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
