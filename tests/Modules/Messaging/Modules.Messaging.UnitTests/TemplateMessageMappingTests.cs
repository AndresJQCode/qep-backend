using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.2: <c>details</c> = <c>{ template: { name, language, category, parameters } }</c> y
/// <c>MessageDto.template</c> sale de ahí sólo para un mensaje <c>Template</c>.</summary>
public sealed class TemplateMessageMappingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly string[] DetailProperties = ["name", "language", "category"];

    private static MessageRow Row(MessageKind kind, string? details) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), MessageDirection.Outbound, kind, "texto", null, details, MessageStatus.Sent, null,
            DateTimeOffset.UnixEpoch, null, null, null);

    private static MessageDto Map(MessageRow row) =>
        MessageMapping.ToDto(row, TenantId, new Dictionary<Guid, string>(), new Dictionary<Guid, ReplyTargetRow>());

    [Fact]
    public void TheDetailHoldsNameLanguageCategoryAndTheValuesByKey()
    {
        var json = TemplateDetailsJson.Serialize("order_update", "es", "UTILITY", new Dictionary<string, string> { ["first_name"] = "Ana", ["order_id"] = "A-1" });

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var template = document.RootElement.GetProperty("template");
        Assert.Equal(["order_update", "es", "UTILITY"], DetailProperties.Select(property => template.GetProperty(property).GetString()));
        Assert.Equal("Ana", template.GetProperty("parameters").GetProperty("first_name").GetString());
        Assert.Equal("A-1", template.GetProperty("parameters").GetProperty("order_id").GetString());
    }

    [Fact]
    public void ATemplateMessageExposesItsNameAndLanguage()
    {
        var json = TemplateDetailsJson.Serialize("order_update", "es", "UTILITY", new Dictionary<string, string>());

        var dto = Map(Row(MessageKind.Template, json));

        Assert.Equal(new MessageTemplateRefDto("order_update", "es"), dto.Template);
        Assert.Equal("Template", dto.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("{}")]
    [InlineData("""{"template":{"name":"x"}}""")]
    [InlineData("""{"template":"x"}""")]
    public void ATemplateMessageWithAnUnreadableDetailHasNoTemplateInsteadOfBreakingThePage(string? details) =>
        Assert.Null(Map(Row(MessageKind.Template, details)).Template);

    [Fact]
    public void AMessageThatIsNotATemplateNeverHasOne()
    {
        var json = TemplateDetailsJson.Serialize("order_update", "es", "UTILITY", new Dictionary<string, string>());

        Assert.Null(Map(Row(MessageKind.Text, json)).Template);
    }

    [Fact]
    public void ATemplateMessageExposesItsLinkHeader()
    {
        var json = TemplateDetailsJson.Serialize(
            "aviso_logo", "es", "UTILITY", new Dictionary<string, string>(),
            TemplateHeader.ByLink("image", "https://cdn.example.com/logo.png", null));

        Assert.Equal(
            new MessageTemplateRefDto("aviso_logo", "es", new MessageTemplateHeaderDto("image", "https://cdn.example.com/logo.png", null)),
            Map(Row(MessageKind.Template, json)).Template);
    }

    [Theory]
    [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"image","link":"http://cdn.example.com/logo.png"}}}""")]
    [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"image","link":"/logo.png"}}}""")]
    [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"location","link":"https://cdn.example.com/a"}}}""")]
    [InlineData("""{"template":{"name":"x","language":"es","header":{"type":"image"}}}""")]
    [InlineData("""{"template":{"name":"x","language":"es","header":"x"}}""")]
    public void AStoredHeaderOutsideTheContractIsHidden(string details) =>
        Assert.Equal(new MessageTemplateRefDto("x", "es"), Map(Row(MessageKind.Template, details)).Template);
}
