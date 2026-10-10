using System.Text.Json;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.7 (mapa de tipos), §8.2 (statuses, account_update, campos desconocidos) y
/// Review Focus 1: lo roto se salta, lo desconocido es Unsupported, nunca una excepción.</summary>
public sealed class WebhookPayloadParserTests
{
    private static string Envelope(string field, object value) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[] { new { id = "222", changes = new[] { new { field, value } } } },
    });

    private static object Messages(object message, object? contact = null) => new
    {
        messaging_product = "whatsapp",
        metadata = new { display_phone_number = "15550000000", phone_number_id = "111" },
        contacts = new[] { contact ?? new { profile = new { name = "Laura" }, wa_id = "573001234567" } },
        messages = new[] { message },
    };

    [Theory]
    [InlineData("text", MessageKind.Text)]
    [InlineData("image", MessageKind.Image)]
    [InlineData("video", MessageKind.Video)]
    [InlineData("audio", MessageKind.Audio)]
    [InlineData("document", MessageKind.Document)]
    [InlineData("sticker", MessageKind.Sticker)]
    [InlineData("location", MessageKind.Location)]
    [InlineData("contacts", MessageKind.Contacts)]
    [InlineData("reaction", MessageKind.Reaction)]
    [InlineData("interactive", MessageKind.Interactive)]
    [InlineData("button", MessageKind.Interactive)]
    [InlineData("unsupported", MessageKind.Unsupported)]
    [InlineData("order", MessageKind.Unsupported)]
    [InlineData("system", MessageKind.Unsupported)]
    [InlineData("whatever", MessageKind.Unsupported)]
    [InlineData(null, MessageKind.Unsupported)]
    public void EveryMetaTypeHasItsKind(string? type, MessageKind expected) =>
        Assert.Equal(expected, MessageKindMap.FromMetaType(type));

    [Fact]
    public void ATextMessageCarriesTheContactTheTimestampAndTheBody()
    {
        var json = Envelope("messages", Messages(new { from = "573001234567", id = "wamid.1", timestamp = "1760000000", type = "text", text = new { body = "hola" } }));

        var change = Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json)));
        var message = Assert.Single(change.Messages);

        Assert.Equal("111", change.PhoneNumberId);
        Assert.Equal(("wamid.1", "573001234567", "Laura", MessageKind.Text, "hola"), (message.Wamid, message.WaId, message.ProfileName, message.Kind, message.Text));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), message.OccurredAt);
        Assert.Null(message.Media);
        Assert.Empty(change.Statuses);
    }

    // Un valor más largo que su columna de message_media haría fallar el INSERT en cada intento.
    [Fact]
    public void OverlongMediaFieldsAreTruncatedToTheirColumns()
    {
        var json = Envelope("messages", Messages(new
        {
            from = "573001234567",
            id = "wamid.doc",
            timestamp = "1760000000",
            type = "document",
            document = new { id = "media-1", mime_type = new string('m', 300), sha256 = new string('s', 300), filename = new string('f', 300) },
        }));

        var media = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages).Media!;

        Assert.Equal((128, 64, 256), (media.MimeType.Length, media.Sha256!.Length, media.FileName!.Length));
    }

    [Fact]
    public void AnImageWithCaptionGoesToCaptionAndMedia()
    {
        var json = Envelope("messages", Messages(new { from = "573001234567", id = "wamid.2", timestamp = "1760000000", type = "image", image = new { id = "media-1", mime_type = "image/jpeg", sha256 = "abc", caption = "la foto" } }));

        var message = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);

        Assert.Equal(MessageKind.Image, message.Kind);
        Assert.Null(message.Text);
        Assert.Equal("la foto", message.Caption);
        Assert.Equal(("media-1", "image/jpeg", "abc"), (message.Media!.MetaMediaId, message.Media.MimeType, message.Media.Sha256));
    }

    [Fact]
    public void ADocumentKeepsItsFileNameAndALocationItsDetails()
    {
        var document = Envelope("messages", Messages(new { from = "1", id = "wamid.3", timestamp = "1760000000", type = "document", document = new { id = "m", mime_type = "application/pdf", filename = "orden.pdf" } }));
        var location = Envelope("messages", Messages(new { from = "1", id = "wamid.4", timestamp = "1760000000", type = "location", location = new { latitude = 4.6, longitude = -74.1, name = "Casa", address = "Calle 1" } }));

        var doc = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(document)[0]).Messages);
        var loc = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(location)[0]).Messages);

        Assert.Equal("orden.pdf", doc.Media!.FileName);
        Assert.Equal(MessageKind.Location, loc.Kind);
        using var details = JsonDocument.Parse(loc.DetailsJson!);
        Assert.Equal(4.6, details.RootElement.GetProperty("latitude").GetDouble());
        Assert.Equal("Casa", details.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void ContactsReactionInteractiveAndButtonFillTheTextFromTheirShape()
    {
        var contacts = Envelope("messages", Messages(new { from = "1", id = "w1", timestamp = "1", type = "contacts", contacts = new[] { new { name = new { formatted_name = "Ana" } }, new { name = new { formatted_name = "Luis" } } } }));
        var reaction = Envelope("messages", Messages(new { from = "1", id = "w2", timestamp = "1", type = "reaction", reaction = new { message_id = "wamid.x", emoji = "👍" } }));
        var interactive = Envelope("messages", Messages(new { from = "1", id = "w3", timestamp = "1", type = "interactive", interactive = new { type = "button_reply", button_reply = new { id = "b", title = "Sí" } } }));
        var button = Envelope("messages", Messages(new { from = "1", id = "w4", timestamp = "1", type = "button", button = new { payload = "p", text = "Confirmar" } }));

        Assert.Equal("Ana, Luis", Single(contacts).Text);
        Assert.Equal("👍", Single(reaction).Text);
        Assert.Equal("Sí", Single(interactive).Text);
        Assert.Equal("Confirmar", Single(button).Text);
        Assert.Equal(MessageKind.Interactive, Single(button).Kind);

        static InboundMessage Single(string json) => Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);
    }

    [Fact]
    public void AnUnsupportedMessageKeepsItsTypeAndErrorsInDetails()
    {
        var json = Envelope("messages", Messages(new { from = "1", id = "w5", timestamp = "1", type = "unsupported", errors = new[] { new { code = 131051, title = "Unsupported message type" } } }));

        var message = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);

        Assert.Equal(MessageKind.Unsupported, message.Kind);
        Assert.Contains("\"type\":\"unsupported\"", message.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("131051", message.DetailsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sent", MessageStatus.Sent)]
    [InlineData("delivered", MessageStatus.Delivered)]
    [InlineData("read", MessageStatus.Read)]
    [InlineData("played", MessageStatus.Read)]
    [InlineData("failed", MessageStatus.Failed)]
    public void EveryStatusHasItsValueAndTheCallbackIsParsed(string status, MessageStatus expected)
    {
        var callback = Guid.CreateVersion7();
        var json = Envelope("messages", new
        {
            messaging_product = "whatsapp",
            metadata = new { display_phone_number = "1", phone_number_id = "111" },
            statuses = new[] { new { id = "wamid.out", status, timestamp = "1760000000", recipient_id = "573001234567", biz_opaque_callback_data = $"qep:{callback}", errors = new[] { new { code = 131047, title = "Re-engagement message" } } } },
        });

        var update = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Statuses);

        Assert.Equal(expected, update.Status);
        Assert.Equal(callback, update.CallbackMessageId);
        Assert.Equal(131047, update.ErrorCode);
        Assert.Equal("Re-engagement message", update.ErrorTitle);
    }

    [Theory]
    [InlineData("qep:not-a-guid")]
    [InlineData("other:123")]
    [InlineData(null)]
    public void ACallbackThatIsNotOursIsNull(string? callback)
    {
        var json = Envelope("messages", new { metadata = new { phone_number_id = "111" }, statuses = new[] { new { id = "w", status = "sent", timestamp = "1", biz_opaque_callback_data = callback } } });

        Assert.Null(Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Statuses).CallbackMessageId);
    }

    [Fact]
    public void AccountUpdateAndUnknownFieldsAreTyped()
    {
        var account = Envelope("account_update", new { @event = "DISABLED_UPDATE", ban_info = new { waba_ban_state = "DISABLE" } });
        var unknown = Envelope("smb_message_echoes", new { anything = 1 });

        var update = Assert.IsType<AccountUpdateChange>(Assert.Single(WebhookPayloadParser.Parse(account)));
        Assert.Equal(("222", "DISABLED_UPDATE", "DISABLE"), (update.WabaId, update.Event, update.BanState));
        Assert.Equal("smb_message_echoes", Assert.IsType<UnknownChange>(Assert.Single(WebhookPayloadParser.Parse(unknown))).Field);
    }

    // Review Focus 1: lo roto no tumba al worker.
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"entry":[]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"messages":[{"from":"1","id":"w","timestamp":"abc","type":"text","text":{"body":"x"}}]}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"messages":[{"id":"w","timestamp":"1","type":"text"}]}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"111"},"statuses":[{"status":"sent"}]}}]}]}""")]
    public void BrokenShapesAreSkippedNeverThrown(string json)
    {
        var changes = WebhookPayloadParser.Parse(json);

        Assert.All(changes.OfType<MessagesChange>(), change =>
        {
            Assert.Empty(change.Messages);
            Assert.Empty(change.Statuses);
        });
    }

    // Formas que JsonElement.TryGetProperty o FromUnixTimeSeconds harían lanzar si no se revisa el tipo antes.
    [Theory]
    [InlineData("""{"entry":"x"}""")]
    [InlineData("""{"entry":[1,{"id":"222","changes":[2,{"field":"messages","value":[]}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"account_update","value":{"event":"REINSTATE","ban_info":"x"}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"metadata":"x","contacts":[1,{"wa_id":"1","profile":"x"}],"messages":[7,{"from":"1","id":"w","timestamp":"99999999999999","type":"text"}],"statuses":["s",{"id":"w","status":"sent","timestamp":-99999999999999}]}}]}]}""")]
    [InlineData("""{"entry":[{"id":"222","changes":[{"field":"messages","value":{"messages":[{"from":"1","id":"w1","timestamp":"1","type":"interactive","interactive":"x"},{"from":"1","id":"w2","timestamp":"1","type":"interactive","interactive":{"button_reply":"x"}},{"from":"1","id":"w3","timestamp":"1","type":"contacts","contacts":[1,{"name":"x"}]},{"from":"1","id":"w4","timestamp":"1","type":"text","text":"x"},{"from":"1","id":"w5","timestamp":"1","type":"image","image":"x"}],"statuses":[{"id":"w","status":"failed","timestamp":"1","errors":[{"code":"x"}]}]}}]}]}""")]
    public void HostileShapesNeverThrow(string json)
    {
        var exception = Record.Exception(() => WebhookPayloadParser.Parse(json));

        Assert.Null(exception);
    }

    [Fact]
    public void AMessageWithoutAMatchingContactStillHasItsWaIdFromFrom()
    {
        var json = Envelope("messages", new { metadata = new { phone_number_id = "111" }, contacts = Array.Empty<object>(), messages = new[] { new { from = "573009999999", id = "w", timestamp = "1", type = "text", text = new { body = "x" } } } });

        var message = Assert.Single(Assert.IsType<MessagesChange>(WebhookPayloadParser.Parse(json)[0]).Messages);

        Assert.Equal("573009999999", message.WaId);
        Assert.Null(message.ProfileName);
    }

    // Un elemento roto en el arreglo no se lleva a sus hermanos: un from vacío o un timestamp ilegible se
    // salta y el válido de al lado entra.
    [Fact]
    public void ABadEntryIsSkippedAndItsValidSiblingSurvives()
    {
        var json = Envelope("messages", new
        {
            metadata = new { phone_number_id = "111" },
            messages = new object[]
            {
                new { from = string.Empty, id = "w.empty-from", timestamp = "1", type = "text", text = new { body = "x" } },
                new { from = "573001234567", id = "w.bad-timestamp", timestamp = "abc", type = "text", text = new { body = "x" } },
                new { from = "573001234567", id = "w.ok", timestamp = "1", type = "text", text = new { body = "ok" } },
            },
        });

        var message = Assert.Single(Assert.IsType<MessagesChange>(Assert.Single(WebhookPayloadParser.Parse(json))).Messages);

        Assert.Equal(("w.ok", "573001234567", "ok"), (message.Wamid, message.WaId, message.Text));
    }

    [Fact]
    public void InvalidJsonIsEmpty() => Assert.Empty(WebhookPayloadParser.Parse("not json"));
}
