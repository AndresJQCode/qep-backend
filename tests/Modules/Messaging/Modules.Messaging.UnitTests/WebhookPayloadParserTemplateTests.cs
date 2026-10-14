// tests/Modules/Messaging/Modules.Messaging.UnitTests/WebhookPayloadParserTemplateTests.cs
using Modules.Messaging.Infrastructure.Webhook;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.3 «Webhook»: <c>message_template_status_update</c> trae event, message_template_id (número o string), nombre,
/// idioma y reason; sin id o sin event cae en UnknownChange; los otros campos de plantilla siguen ignorados.</summary>
public sealed class WebhookPayloadParserTemplateTests
{
    private static string Payload(string valueJson, string wabaId = "222") =>
        $$$"""{"object":"whatsapp_business_account","entry":[{"id":"{{{wabaId}}}","changes":[{"field":"message_template_status_update","value":{{{valueJson}}}}]}]}""";

    [Fact]
    public void AnApprovedEventIsReadWithEveryField()
    {
        var change = Assert.Single(WebhookPayloadParser.Parse(Payload(
            """{"event":"APPROVED","message_template_id":"1407680676729941","message_template_name":"promo","message_template_language":"es","reason":"NONE"}""")));

        var status = Assert.IsType<TemplateStatusChange>(change);
        Assert.Equal(("222", "1407680676729941", "promo", "es", "APPROVED", "NONE"), (status.WabaId, status.MetaTemplateId, status.Name, status.Language, status.Event, status.Reason));
    }

    // Review Focus 1: Meta manda el id como número.
    [Fact]
    public void AMessageTemplateIdThatComesAsANumberIsRead()
    {
        var change = Assert.Single(WebhookPayloadParser.Parse(Payload(
            """{"event":"REJECTED","message_template_id":1407680676729941,"message_template_name":"promo","message_template_language":"es","reason":"INVALID_FORMAT"}""")));

        var status = Assert.IsType<TemplateStatusChange>(change);
        Assert.Equal(("1407680676729941", "REJECTED", "INVALID_FORMAT"), (status.MetaTemplateId, status.Event, status.Reason));
    }

    [Fact]
    public void AMissingReasonOrNameIsNull()
    {
        var status = Assert.IsType<TemplateStatusChange>(Assert.Single(WebhookPayloadParser.Parse(Payload("""{"event":"PAUSED","message_template_id":7}"""))));
        Assert.Equal((null, null, null), (status.Name, status.Language, status.Reason));
    }

    [Theory]
    [InlineData("""{"event":"APPROVED"}""")]
    [InlineData("""{"message_template_id":7}""")]
    [InlineData("""{"event":"","message_template_id":7}""")]
    [InlineData("""{"event":"APPROVED","message_template_id":true}""")]
    public void WithoutIdOrEventItIsAnUnknownChange(string value)
    {
        var change = Assert.Single(WebhookPayloadParser.Parse(Payload(value)));
        Assert.Equal("message_template_status_update", Assert.IsType<UnknownChange>(change).Field);
    }

    [Theory]
    [InlineData("message_template_quality_update")]
    [InlineData("template_category_update")]
    public void TheOtherTemplateFieldsStayIgnored(string field)
    {
        var json = $$$"""{"object":"whatsapp_business_account","entry":[{"id":"222","changes":[{"field":"{{{field}}}","value":{"message_template_id":7}}]}]}""";
        var change = Assert.Single(WebhookPayloadParser.Parse(json));
        Assert.Equal(field, Assert.IsType<UnknownChange>(change).Field);
    }
}
