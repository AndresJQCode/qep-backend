using System.Text.Json;
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.1 con los JSON de ejemplo de <c>GET /{waba}/message_templates</c>: sólo APPROVED, parámetros en
/// orden de aparición, nombradas o posicionales, y las que v1 no envía con su motivo.</summary>
public sealed class TemplateCatalogTests
{
    private static IReadOnlyList<MessageTemplateDto> Normalize(string json) =>
        TemplateCatalog.Normalize(JsonDocument.Parse(json).RootElement.EnumerateArray().Select(element => element.Clone()).ToArray());

    private static MessageTemplateDto Single(string json) => Assert.Single(Normalize("[" + json + "]"));

    private const string NamedUtility = """
        {
          "id": "1", "name": "order_update", "language": "es", "status": "APPROVED", "category": "UTILITY", "parameter_format": "NAMED",
          "components": [
            { "type": "HEADER", "format": "TEXT", "text": "Tu pedido" },
            { "type": "BODY", "text": "Hola {{first_name}}, tu pedido {{order_id}} salió. Gracias, {{first_name}}.",
              "example": { "body_text_named_params": [ { "param_name": "first_name", "example": "Ana" }, { "param_name": "order_id", "example": "A-1" } ] } },
            { "type": "FOOTER", "text": "QCode" },
            { "type": "BUTTONS", "buttons": [
              { "type": "QUICK_REPLY", "text": "Detener promociones" },
              { "type": "URL", "text": "Ver pedido", "url": "https://qcode.co/pedidos" },
              { "type": "PHONE_NUMBER", "text": "Llamar", "phone_number": "+576015550100" } ] }
          ]
        }
        """;

    [Fact]
    public void ANamedTemplateKeepsItsPartsAndTheKeysInOrderOfAppearanceWithoutRepeats()
    {
        var template = Single(NamedUtility);

        Assert.Equal("order_update", template.Name);
        Assert.Equal("es", template.Language);
        Assert.Equal("UTILITY", template.Category);
        Assert.Equal("Named", template.ParameterFormat);
        Assert.Equal("Tu pedido", template.Header);
        Assert.Equal("Hola {{first_name}}, tu pedido {{order_id}} salió. Gracias, {{first_name}}.", template.Body);
        Assert.Equal("QCode", template.Footer);
        Assert.Equal(["first_name", "order_id"], template.Parameters);
        Assert.True(template.Sendable);
        Assert.Null(template.UnsupportedReason);
    }

    [Fact]
    public void TheButtonsKeepTheirTypeTextAndOnlyTheUrlOfAUrlButton()
    {
        var buttons = Single(NamedUtility).Buttons;

        Assert.Equal(
            [new TemplateButtonDto("QuickReply", "Detener promociones", null), new TemplateButtonDto("Url", "Ver pedido", "https://qcode.co/pedidos"), new TemplateButtonDto("PhoneNumber", "Llamar", null)],
            buttons);
    }

    [Fact]
    public void APositionalTemplateHasNumericKeysAndNoOptionalParts()
    {
        var template = Single("""
            { "name": "hola", "language": "es_CO", "status": "APPROVED", "category": "MARKETING", "parameter_format": "POSITIONAL",
              "components": [ { "type": "BODY", "text": "Hola {{1}}, tienes {{2}} puntos" } ] }
            """);

        Assert.Equal("Positional", template.ParameterFormat);
        Assert.Equal(["1", "2"], template.Parameters);
        Assert.Null(template.Header);
        Assert.Null(template.Footer);
        Assert.Empty(template.Buttons);
        Assert.True(template.Sendable);
    }

    [Fact]
    public void ATemplateWithoutParameterFormatIsPositionalAndOneWithoutVariablesHasNoParameters()
    {
        var template = Single("""
            { "name": "fijo", "language": "es", "status": "APPROVED", "category": "UTILITY",
              "components": [ { "type": "BODY", "text": "Estamos abiertos hasta las 6" } ] }
            """);

        Assert.Equal("Positional", template.ParameterFormat);
        Assert.Empty(template.Parameters);
        Assert.True(template.Sendable);
    }

    [Fact]
    public void OnlyApprovedTemplatesSurviveAndTheyComeByNameThenLanguage()
    {
        var templates = Normalize("""
            [
              { "name": "b", "language": "es", "status": "APPROVED", "category": "UTILITY", "components": [ { "type": "BODY", "text": "b" } ] },
              { "name": "a", "language": "es", "status": "REJECTED", "category": "UTILITY", "components": [ { "type": "BODY", "text": "a" } ] },
              { "name": "a", "language": "pt_BR", "status": "APPROVED", "category": "UTILITY", "components": [ { "type": "BODY", "text": "a" } ] },
              { "name": "a", "language": "es", "status": "APPROVED", "category": "UTILITY", "components": [ { "type": "BODY", "text": "a" } ] },
              { "name": "c", "language": "es", "status": "PAUSED", "category": "UTILITY", "components": [ { "type": "BODY", "text": "c" } ] },
              { "name": "d", "language": "es", "status": "PENDING", "category": "UTILITY", "components": [ { "type": "BODY", "text": "d" } ] }
            ]
            """);

        Assert.Equal(["a/es", "a/pt_BR", "b/es"], templates.Select(template => $"{template.Name}/{template.Language}"));
    }

    [Theory]
    [InlineData("IMAGE")]
    [InlineData("VIDEO")]
    [InlineData("DOCUMENT")]
    [InlineData("LOCATION")]
    public void AMediaHeaderIsNotSendable(string format)
    {
        var template = Single($$"""
            { "name": "promo", "language": "es", "status": "APPROVED", "category": "MARKETING",
              "components": [ { "type": "HEADER", "format": "{{format}}", "example": { "header_handle": [ "https://scontent.example/x" ] } },
                              { "type": "BODY", "text": "Mira esto" } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("media_header", template.UnsupportedReason);
        Assert.Null(template.Header);
    }

    [Fact]
    public void ADynamicUrlButtonIsSendableAndItsSuffixIsAParameterAfterTheBody()
    {
        var template = Single("""
            { "name": "track", "language": "es", "status": "APPROVED", "category": "UTILITY",
              "components": [ { "type": "BODY", "text": "Hola {{1}}, tu guía" },
                              { "type": "BUTTONS", "buttons": [ { "type": "QUICK_REPLY", "text": "Gracias" },
                                                                { "type": "URL", "text": "Rastrear", "url": "https://qcode.co/t/{{1}}", "example": [ "https://qcode.co/t/123" ] } ] } ] }
            """);

        Assert.True(template.Sendable);
        Assert.Null(template.UnsupportedReason);
        Assert.Equal(["1", "button:1"], template.Parameters);
        Assert.Equal(new TemplateButtonDto("Url", "Rastrear", "https://qcode.co/t/{{1}}", "button:1"), template.Buttons[1]);
        Assert.Null(template.Buttons[0].Parameter);
    }

    [Theory]
    [InlineData("https://qcode.co/{{1}}/t/{{2}}")]
    [InlineData("https://qcode.co/t/{{1}}?x=1")]
    public void AUrlMetaWouldNotFillIsStillNotSendable(string url)
    {
        var template = Single($$"""
            { "name": "track", "language": "es", "status": "APPROVED", "category": "UTILITY",
              "components": [ { "type": "BODY", "text": "Tu guía" },
                              { "type": "BUTTONS", "buttons": [ { "type": "URL", "text": "Rastrear", "url": "{{url}}" } ] } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("dynamic_url_button", template.UnsupportedReason);
    }

    [Fact]
    public void BuildSendSeparatesTheBodyFromTheUrlButtons()
    {
        var template = Single("""
            { "name": "track", "language": "es", "status": "APPROVED", "category": "UTILITY",
              "components": [ { "type": "BODY", "text": "Hola {{1}}" },
                              { "type": "BUTTONS", "buttons": [ { "type": "URL", "text": "Rastrear", "url": "https://qcode.co/t/{{1}}" } ] } ] }
            """);

        var built = TemplateCatalog.BuildSend(template, new Dictionary<string, string> { ["1"] = "Ana", ["button:0"] = "A-1" });

        Assert.Equal([new TemplateParameter("1", "Ana")], built.Send.Parameters);
        Assert.Equal([new TemplateUrlButton(0, "A-1")], built.Send.UrlButtons!);
        Assert.Equal("Hola Ana", built.Text);
    }

    [Fact]
    public void AnAuthenticationTemplateIsNotSendable()
    {
        var template = Single("""
            { "name": "codigo", "language": "es", "status": "APPROVED", "category": "AUTHENTICATION",
              "components": [ { "type": "BODY", "add_security_recommendation": true },
                              { "type": "BUTTONS", "buttons": [ { "type": "OTP", "otp_type": "COPY_CODE", "text": "Copiar código" } ] } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("authentication", template.UnsupportedReason);
        Assert.Equal("", template.Body);
        Assert.Equal("Other", Assert.Single(template.Buttons).Type);
    }

    [Fact]
    public void ACarouselIsNotSendable()
    {
        var template = Single("""
            { "name": "catalogo", "language": "es", "status": "APPROVED", "category": "MARKETING",
              "components": [ { "type": "BODY", "text": "Mira" }, { "type": "CAROUSEL", "cards": [ { "components": [ { "type": "HEADER", "format": "IMAGE" } ] } ] } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("carousel", template.UnsupportedReason);
    }

    [Fact]
    public void AFlowButtonIsNotSendable()
    {
        var template = Single("""
            { "name": "encuesta", "language": "es", "status": "APPROVED", "category": "MARKETING",
              "components": [ { "type": "BODY", "text": "Responde" }, { "type": "BUTTONS", "buttons": [ { "type": "FLOW", "text": "Abrir", "flow_id": "9" } ] } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("flow", template.UnsupportedReason);
    }

    [Theory]
    [InlineData("COPY_CODE")]
    [InlineData("MPM")]
    [InlineData("CATALOG")]
    public void AnyOtherButtonThatNeedsMoreThanTextIsNotSendable(string type)
    {
        var template = Single($$"""
            { "name": "x", "language": "es", "status": "APPROVED", "category": "MARKETING",
              "components": [ { "type": "BODY", "text": "Hola" }, { "type": "BUTTONS", "buttons": [ { "type": "{{type}}", "text": "Ir" } ] } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("other", template.UnsupportedReason);
        Assert.Equal("Other", Assert.Single(template.Buttons).Type);
    }

    [Fact]
    public void ATextHeaderWithAVariableIsNotSendable()
    {
        var template = Single("""
            { "name": "x", "language": "es", "status": "APPROVED", "category": "UTILITY",
              "components": [ { "type": "HEADER", "format": "TEXT", "text": "Hola {{1}}" }, { "type": "BODY", "text": "Cuerpo" } ] }
            """);

        Assert.False(template.Sendable);
        Assert.Equal("other", template.UnsupportedReason);
    }

    [Fact]
    public void RenderPutsTheValuesInAndJoinsThePartsWithABlankLine()
    {
        var template = Single(NamedUtility);

        var text = TemplateCatalog.Render(template, new Dictionary<string, string> { ["first_name"] = "Ana", ["order_id"] = "A-1" });

        Assert.Equal("Tu pedido\n\nHola Ana, tu pedido A-1 salió. Gracias, Ana.\n\nQCode", text);
    }

    [Fact]
    public void BuildSendGivesTheMetaPayloadTheRenderedTextAndTheDetailsInOneStep()
    {
        // R10: el envío humano y el worker arman lo mismo; una sola función.
        var template = Single(NamedUtility);
        var values = new Dictionary<string, string> { ["first_name"] = "Ana", ["order_id"] = "A-1" };

        var built = TemplateCatalog.BuildSend(template, values);

        Assert.Equal(TemplateCatalog.Render(template, values), built.Text);
        Assert.Equal(("order_update", "es", true), (built.Send.Name, built.Send.Language, built.Send.Named));
        Assert.Equal([new TemplateParameter("first_name", "Ana"), new TemplateParameter("order_id", "A-1")], built.Send.Parameters);
        Assert.Equal(TemplateDetailsJson.Serialize("order_update", "es", "UTILITY", values), built.DetailsJson);
    }

    [Fact]
    public void RenderOmitsTheMissingParts()
    {
        var template = Single("""
            { "name": "hola", "language": "es", "status": "APPROVED", "category": "MARKETING",
              "components": [ { "type": "BODY", "text": "Hola {{1}}, tienes {{2}} puntos" } ] }
            """);

        Assert.Equal("Hola Ana, tienes 12 puntos", TemplateCatalog.Render(template, new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "12" }));
    }
}
