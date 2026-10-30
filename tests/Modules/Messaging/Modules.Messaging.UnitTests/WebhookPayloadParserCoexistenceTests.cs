// tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserCoexistenceTests.cs
using System.Globalization;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-29: <c>history</c>, <c>smb_message_echoes</c> y <c>smb_app_state_sync</c>. Los campos marcados V-1 en
/// <c>MetaPayloads</c> no están verificados contra Meta real.</summary>
public sealed class WebhookPayloadParserCoexistenceTests
{
    private static readonly string[] CoexistenceFields = ["history", "smb_message_echoes", "smb_app_state_sync"];

    private static string Payload(string field, string valueJson) =>
        $$$"""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"{{{field}}}","value":{{{valueJson}}}}]}]}""";

    private static string HistoryValue(string chunkMetadata, string threads, string chunkExtra = "") =>
        $$$"""{"messaging_product":"whatsapp","metadata":{"display_phone_number":"15550001111","phone_number_id":"PNID"},"history":[{"metadata":{{{chunkMetadata}}},"threads":{{{threads}}}{{{chunkExtra}}}}]}""";

    [Fact]
    public void AHistoryChunkCarriesPhaseOrderProgressAndThreads()
    {
        var json = Payload("history", HistoryValue(
            """{"phase":1,"chunk_order":2,"progress":40}""",
            """[{"id":"573001112233","messages":[{"from":"573001112233","id":"wamid.IN","timestamp":"1761700000","type":"text","text":{"body":"hola"}},{"from":"15550001111","to":"573001112233","id":"wamid.OUT","timestamp":"1761700060","type":"text","text":{"body":"buenas"}}]}]"""));

        var change = Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(json)));

        Assert.Equal("PNID", change.PhoneNumberId);
        Assert.Empty(change.ErrorCodes);
        var chunk = Assert.Single(change.Chunks);
        Assert.Equal((1, 2, 40, 0), (chunk.Phase, chunk.ChunkOrder, chunk.Progress, chunk.SkippedMessages));
        var thread = Assert.Single(chunk.Threads);
        Assert.Equal("573001112233", thread.WaId);
        Assert.Collection(
            thread.Messages,
            inbound =>
            {
                Assert.Equal(("wamid.IN", MessageDirection.Inbound, MessageKind.Text, "hola"), (inbound.Wamid, inbound.Direction, inbound.Kind, inbound.Text));
                Assert.Equal(DateTimeOffset.Parse("2025-10-29T01:06:40Z", CultureInfo.InvariantCulture), inbound.OccurredAt);
            },
            outbound => Assert.Equal(("wamid.OUT", MessageDirection.Outbound, MessageKind.Text, "buenas"), (outbound.Wamid, outbound.Direction, outbound.Kind, outbound.Text)));
    }

    [Fact]
    public void ADeclinedHistoryCarriesTheErrorCode()
    {
        // V-1: unverified. El error puede venir en value.errors o en history[].errors; el parser acepta los dos.
        var atValue = Payload("history", """{"metadata":{"phone_number_id":"PNID"},"errors":[{"code":2593109,"title":"declined"}]}""");
        var atChunk = Payload("history", HistoryValue("""{}""", "[]", ",\"errors\":[{\"code\":2593109}]"));

        Assert.Equal([WebhookPayloadParser.HistoryDeclinedCode], Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(atValue))).ErrorCodes);
        Assert.Equal([2593109], Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(atChunk))).ErrorCodes);
    }

    [Fact]
    public void AMediaPlaceholderIsUnsupportedWithoutMedia()
    {
        var json = Payload("history", HistoryValue(
            """{"phase":0,"chunk_order":1,"progress":10}""",
            """[{"id":"573001112233","messages":[{"from":"573001112233","id":"wamid.M","timestamp":"1761700000","type":"media_placeholder"}]}]"""));

        var message = Assert.Single(Assert.Single(Assert.Single(Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Chunks).Threads).Messages);

        Assert.Equal(MessageKind.Unsupported, message.Kind);
        Assert.True(message.IsMediaPlaceholder);
        Assert.Null(message.Media);
        Assert.Contains("historyMedia", message.DetailsJson);
    }

    // Review Focus 2.
    [Fact]
    public void AThreadIdIsNormalizedToDigitsAndAnInvalidOneIsSkipped()
    {
        var json = Payload("history", HistoryValue(
            """{"phase":1,"chunk_order":1,"progress":5}""",
            """[{"id":"+57 300 111 2233","messages":[{"from":"573001112233","id":"wamid.A","timestamp":"1761700000","type":"text","text":{"body":"a"}}]},{"id":"abc","messages":[{"from":"x","id":"wamid.B","timestamp":"1761700000","type":"text","text":{"body":"b"}}]},{"id":"573009998877","messages":[{"from":"573009998877","id":"wamid.C","timestamp":"1761700000","type":"text","text":{"body":"c"}}]}]"""));

        var chunk = Assert.Single(Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Chunks);

        Assert.Equal(["573001112233", "573009998877"], chunk.Threads.Select(thread => thread.WaId));
    }

    // Review Focus 5.
    [Fact]
    public void AMessageWithoutTimestampIsSkippedAndTheChunkKeepsItsProgress()
    {
        var json = Payload("history", HistoryValue(
            """{"phase":1,"chunk_order":3,"progress":40}""",
            """[{"id":"573001112233","messages":[{"from":"573001112233","id":"wamid.NOTS","type":"text","text":{"body":"sin fecha"}},{"from":"573001112233","id":"wamid.OK","timestamp":"1761700000","type":"text","text":{"body":"ok"}}]}]"""));

        var chunk = Assert.Single(Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Chunks);

        Assert.Equal(1, chunk.SkippedMessages);
        Assert.Equal(40, chunk.Progress);
        Assert.Equal("wamid.OK", Assert.Single(Assert.Single(chunk.Threads).Messages).Wamid);
    }

    // Decisión del controlador R5: fase y progreso fuera de rango nunca llegan al dominio; los mensajes se conservan.
    [Theory]
    [InlineData("""{"phase":3,"chunk_order":1,"progress":50}""", null, 1, 50)]
    [InlineData("""{"phase":-1,"chunk_order":1,"progress":50}""", null, 1, 50)]
    [InlineData("""{"phase":2,"chunk_order":1,"progress":101}""", 2, 1, null)]
    [InlineData("""{"phase":2,"chunk_order":1,"progress":-5}""", 2, 1, null)]
    [InlineData("""{"phase":99999999999,"chunk_order":-2,"progress":1e9}""", null, null, null)]
    [InlineData("""{"phase":0,"chunk_order":0,"progress":100}""", 0, 0, 100)]
    public void OutOfRangeChunkMetadataIsDroppedButTheMessagesAreKept(string metadata, int? phase, int? order, int? progress)
    {
        var json = Payload("history", HistoryValue(
            metadata,
            """[{"id":"573001112233","messages":[{"from":"573001112233","id":"wamid.A","timestamp":"1761700000","type":"text","text":{"body":"a"}}]}]"""));

        var chunk = Assert.Single(Assert.IsType<HistoryChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Chunks);

        Assert.Equal((phase, order, progress), (chunk.Phase, chunk.ChunkOrder, chunk.Progress));
        Assert.Single(Assert.Single(chunk.Threads).Messages);
    }

    [Fact]
    public void AnEchoIsOutboundToTheUserPhone()
    {
        var json = Payload("smb_message_echoes", """
            {"messaging_product":"whatsapp","metadata":{"display_phone_number":"15550001111","phone_number_id":"PNID"},
             "message_echoes":[{"from":"15550001111","to":"573001112233","id":"wamid.E","timestamp":"1761700100","type":"image","image":{"id":"MID","mime_type":"image/jpeg","caption":"foto"}}]}
            """);

        var change = Assert.IsType<SmbEchoesChange>(Assert.Single(WebhookPayloadParser.Parse(json)));

        Assert.Equal("PNID", change.PhoneNumberId);
        var (waId, message) = Assert.Single(change.Echoes);
        Assert.Equal("573001112233", waId);
        Assert.Equal((MessageDirection.Outbound, MessageKind.Image, "foto"), (message.Direction, message.Kind, message.Caption));
        Assert.Equal("MID", message.Media?.MetaMediaId);
    }

    [Fact]
    public void ContactsSyncReadsAddAndRemove()
    {
        var json = Payload("smb_app_state_sync", """
            {"messaging_product":"whatsapp","metadata":{"display_phone_number":"15550001111","phone_number_id":"PNID"},
             "state_sync":[
               {"type":"contact","contact":{"full_name":"Ana Pérez","first_name":"Ana","phone_number":"+573001112233"},"action":"add","metadata":{"timestamp":"1761700000"}},
               {"type":"contact","contact":{"full_name":"Luis","first_name":"Luis","phone_number":"573009998877"},"action":"remove","metadata":{"timestamp":"1761700001"}},
               {"type":"other","contact":{"phone_number":"573000000000"},"action":"add"}]}
            """);

        var change = Assert.IsType<ContactsSyncChange>(Assert.Single(WebhookPayloadParser.Parse(json)));

        Assert.Equal("PNID", change.PhoneNumberId);
        Assert.Equal(
            [new SyncedContact("573001112233", "Ana Pérez", "Ana", false), new SyncedContact("573009998877", "Luis", "Luis", true)],
            change.Contacts);
    }

    [Fact]
    public void ABodyThatIsNotWellFormedNeverThrows()
    {
        Assert.All(
            CoexistenceFields,
            field =>
            {
                Assert.Single(WebhookPayloadParser.Parse(Payload(field, """{"history":"x","message_echoes":[1,null],"state_sync":[[]]}""")));
                Assert.Single(WebhookPayloadParser.Parse(Payload(field, "null")));
            });
    }
}
