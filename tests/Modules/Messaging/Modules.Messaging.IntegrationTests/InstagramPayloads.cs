using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Cuerpos del webhook de Instagram con la forma literal de Meta (spec 2026-10-14 §3):
/// <c>{"object":"instagram","entry":[{"id":"&lt;cuenta&gt;","time":…,"messaging":[{sender, recipient, timestamp, message | read |
/// reaction | postback}]}]}</c>. El <c>timestamp</c> va en milisegundos.</summary>
internal static class InstagramPayloads
{
    public const string DefaultAccountId = "17841400000000001";

    public const string DefaultUserId = "1234567890123456";

    public static string Text(string mid, long timestampSeconds, string text, string userId = DefaultUserId, string accountId = DefaultAccountId, string? replyToMid = null)
    {
        var message = new JsonObject { ["mid"] = mid, ["text"] = text };
        if (replyToMid is not null)
        {
            message["reply_to"] = new JsonObject { ["mid"] = replyToMid };
        }

        return Entry(accountId, Event(userId, accountId, timestampSeconds, "message", message));
    }

    public static string Attachment(string mid, long timestampSeconds, string type, string url, string userId = DefaultUserId, string accountId = DefaultAccountId) =>
        Entry(accountId, Event(userId, accountId, timestampSeconds, "message", new JsonObject
        {
            ["mid"] = mid,
            ["attachments"] = new JsonArray(new JsonObject { ["type"] = type, ["payload"] = new JsonObject { ["url"] = url } }),
        }));

    /// <summary>Lo que el negocio respondió desde la app de Instagram: el emisor es la cuenta y el receptor la persona.</summary>
    public static string Echo(string mid, long timestampSeconds, string text, string userId = DefaultUserId, string accountId = DefaultAccountId) =>
        Entry(accountId, Event(accountId, userId, timestampSeconds, "message", new JsonObject { ["mid"] = mid, ["text"] = text, ["is_echo"] = true }));

    public static string Deleted(string mid, long timestampSeconds, string userId = DefaultUserId, string accountId = DefaultAccountId) =>
        Entry(accountId, Event(userId, accountId, timestampSeconds, "message", new JsonObject { ["mid"] = mid, ["is_deleted"] = true }));

    public static string Seen(string mid, long timestampSeconds, string userId = DefaultUserId, string accountId = DefaultAccountId) =>
        Entry(accountId, Event(userId, accountId, timestampSeconds, "read", new JsonObject { ["mid"] = mid }));

    public static string Reaction(string mid, long timestampSeconds, string emoji, string action = "react", string userId = DefaultUserId, string accountId = DefaultAccountId) =>
        Entry(accountId, Event(userId, accountId, timestampSeconds, "reaction", new JsonObject { ["mid"] = mid, ["action"] = action, ["reaction"] = "love", ["emoji"] = emoji }));

    public static string Postback(string mid, long timestampSeconds, string title, string payload, string userId = DefaultUserId, string accountId = DefaultAccountId) =>
        Entry(accountId, Event(userId, accountId, timestampSeconds, "postback", new JsonObject { ["mid"] = mid, ["title"] = title, ["payload"] = payload }));

    /// <summary>Dos eventos en la misma entrada.</summary>
    public static string Many(string accountId, params JsonObject[] events) => Entry(accountId, events);

    public static JsonObject Event(string senderId, string recipientId, long timestampSeconds, string field, JsonObject value) =>
        new()
        {
            ["sender"] = new JsonObject { ["id"] = senderId },
            ["recipient"] = new JsonObject { ["id"] = recipientId },
            ["timestamp"] = timestampSeconds * 1000,
            [field] = value,
        };

    public static string Entry(string accountId, params JsonObject[] events)
    {
        var root = new JsonObject
        {
            ["object"] = "instagram",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["id"] = accountId,
                ["time"] = 1760000000000,
                ["messaging"] = new JsonArray(events),
            }),
        };
        return root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
