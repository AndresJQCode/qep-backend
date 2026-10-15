using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §6.2, el parser de Instagram: tolerante (nunca lanza), un evento por elemento de <c>messaging</c>, el
/// <c>timestamp</c> en milisegundos, los adjuntos en su <c>kind</c>, el echo con la persona como clave, el visto, la reacción, el
/// botón y el borrado.</summary>
public sealed class InstagramWebhookPayloadParserTests
{
    private const string Account = "17841400000000001";
    private const string User = "1234567890123456";

    private static string Entry(string messaging) =>
        $$"""{"object":"instagram","entry":[{"id":"{{Account}}","time":1760000000000,"messaging":[{{messaging}}]}]}""";

    // El campo va aparte del cuerpo: un literal crudo no puede empezar con comillas (se las lleva el delimitador).
    private static string Event(string sender, string recipient, string field, string body) =>
        $$"""{"sender":{"id":"{{sender}}"},"recipient":{"id":"{{recipient}}"},"timestamp":1760000000000,"{{field}}":{{body}}}""";

    [Fact]
    public void ATextDmIsAnInboundTextKeyedByTheSenderWithTheTimestampInSeconds()
    {
        var entries = InstagramWebhookPayloadParser.Parse(Entry(Event(User, Account, "message", """{"mid":"m1","text":"hola","reply_to":{"mid":"m0"}}""")));

        var entry = Assert.Single(entries);
        Assert.Equal(Account, entry.AccountId);
        var inbound = Assert.IsType<InstagramMessageEvent>(Assert.Single(entry.Events));
        Assert.False(inbound.IsEcho);
        Assert.Equal(("m1", User, MessageKind.Text, "hola", "m0"), (inbound.Message.Wamid, inbound.Message.UserId, inbound.Message.Kind, inbound.Message.Text, inbound.Message.QuotedWamid));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), inbound.Message.OccurredAt);
        Assert.Null(inbound.Message.WaId);
    }

    [Theory]
    [InlineData("image", MessageKind.Image)]
    [InlineData("video", MessageKind.Video)]
    [InlineData("audio", MessageKind.Audio)]
    [InlineData("file", MessageKind.Document)]
    public void AMediaAttachmentKeepsItsUrlToCopyItLater(string type, MessageKind kind)
    {
        var entries = InstagramWebhookPayloadParser.Parse(Entry(Event(User, Account, "message", $$$"""{"mid":"m2","attachments":[{"type":"{{{type}}}","payload":{"url":"https://cdn.example/x?sig=1"}}]}""")));

        var inbound = Assert.IsType<InstagramMessageEvent>(Assert.Single(Assert.Single(entries).Events));
        Assert.Equal(kind, inbound.Message.Kind);
        Assert.NotNull(inbound.Message.Media);
        Assert.Equal("https://cdn.example/x?sig=1", inbound.Message.Media!.SourceUrl);
        Assert.Equal(string.Empty, inbound.Message.Media.MetaMediaId);
    }

    [Theory]
    [InlineData("share")]
    [InlineData("ig_post")]
    [InlineData("ig_reel")]
    [InlineData("reel")]
    [InlineData("post")]
    public void ASharedPostIsAShareWithItsUrlAsText(string type)
    {
        var entries = InstagramWebhookPayloadParser.Parse(Entry(Event(User, Account, "message", $$$"""{"mid":"m3","attachments":[{"type":"{{{type}}}","payload":{"url":"https://www.instagram.com/p/abc/"}}]}""")));

        var inbound = Assert.IsType<InstagramMessageEvent>(Assert.Single(Assert.Single(entries).Events));
        Assert.Equal(MessageKind.Share, inbound.Message.Kind);
        Assert.Equal("https://www.instagram.com/p/abc/", inbound.Message.Text);
        Assert.Null(inbound.Message.Media);
    }

    [Fact]
    public void AnUnknownAttachmentIsUnsupportedAndAStoryReplyIsTextWithItsStory()
    {
        var unknown = InstagramWebhookPayloadParser.Parse(Entry(Event(User, Account, "message", """{"mid":"m4","attachments":[{"type":"fallback","payload":{"url":"https://x"}}]}""")));
        Assert.Equal(MessageKind.Unsupported, Assert.IsType<InstagramMessageEvent>(Assert.Single(unknown).Events[0]).Message.Kind);

        var story = InstagramWebhookPayloadParser.Parse(Entry(Event(User, Account, "message", """{"mid":"m5","text":"me encanta","reply_to":{"story":{"url":"https://cdn/story","id":"17900"}}}""")));
        var message = Assert.IsType<InstagramMessageEvent>(Assert.Single(story).Events[0]).Message;
        Assert.Equal(MessageKind.Text, message.Kind);
        Assert.Contains("17900", message.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEchoIsKeyedByTheRecipientAndFlagged()
    {
        var entries = InstagramWebhookPayloadParser.Parse(Entry(Event(Account, User, "message", """{"mid":"m6","text":"gracias","is_echo":true}""")));

        var echo = Assert.IsType<InstagramMessageEvent>(Assert.Single(Assert.Single(entries).Events));
        Assert.True(echo.IsEcho);
        Assert.Equal(User, echo.Message.UserId);
        Assert.Equal("gracias", echo.Message.Text);
    }

    [Fact]
    public void ReadReactionPostbackAndDeletionHaveTheirOwnEvents()
    {
        var entries = InstagramWebhookPayloadParser.Parse(Entry(string.Join(",",
            Event(User, Account, "read", """{"mid":"m_out"}"""),
            Event(User, Account, "reaction", """{"mid":"m_out","action":"react","reaction":"love","emoji":"❤️"}"""),
            Event(User, Account, "reaction", """{"mid":"m_out","action":"unreact"}"""),
            Event(User, Account, "postback", """{"mid":"m7","title":"Ver catálogo","payload":"CATALOG"}"""),
            Event(User, Account, "message", """{"mid":"m1","is_deleted":true}"""))));

        var events = Assert.Single(entries).Events;
        Assert.Equal(5, events.Count);
        var seen = Assert.IsType<InstagramSeenEvent>(events[0]);
        Assert.Equal((User, "m_out"), (seen.UserId, seen.Mid));
        var reaction = Assert.IsType<InstagramMessageEvent>(events[1]).Message;
        Assert.Equal((MessageKind.Reaction, "❤️", "m_out"), (reaction.Kind, reaction.Text, reaction.QuotedWamid));
        Assert.Equal("reaction:m_out:1760000000000", reaction.Wamid);
        Assert.IsType<InstagramIgnoredEvent>(events[2]);
        var postback = Assert.IsType<InstagramMessageEvent>(events[3]).Message;
        Assert.Equal((MessageKind.Interactive, "Ver catálogo", "m7"), (postback.Kind, postback.Text, postback.Wamid));
        Assert.Contains("CATALOG", postback.DetailsJson, StringComparison.Ordinal);
        var deleted = Assert.IsType<InstagramDeletedEvent>(events[4]);
        Assert.Equal((User, "m1"), (deleted.UserId, deleted.Mid));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"object":"instagram"}""")]
    [InlineData("""{"object":"instagram","entry":[{"id":"1"}]}""")]
    [InlineData("""{"object":"instagram","entry":[{"id":"1","messaging":[{"sender":{"id":"2"}}]}]}""")]
    public void MalformedOrEmptyBodiesNeverThrow(string json)
    {
        var entries = InstagramWebhookPayloadParser.Parse(json);

        Assert.All(entries, entry => Assert.All(entry.Events, e => Assert.IsType<InstagramIgnoredEvent>(e)));
    }
}
