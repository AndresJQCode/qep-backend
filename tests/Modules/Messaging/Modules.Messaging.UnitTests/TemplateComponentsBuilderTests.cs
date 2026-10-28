using System.Text.Json;
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.4 «Crear»: el payload de <c>POST /{waba}/message_templates</c>. Variables con nombre en orden de aparición con
/// su ejemplo, encabezado y pie opcionales, los tres tipos de botón, y el encabezado de documento con <c>header_handle</c> (§4.5).</summary>
public sealed class TemplateComponentsBuilderTests
{
    private static TemplateDraft Draft(
        string body = "Hola {{first_name}}, {{product}} tiene {{discount}} hoy. Saludos, {{first_name}}.",
        string? header = "Oferta de octubre",
        string? footer = "Responde DETENER para no recibir promociones.",
        IReadOnlyList<TemplateButtonInput>? buttons = null) =>
        new("promo_octubre", "es", "MARKETING", header, body, footer,
            buttons ?? [new TemplateButtonInput("QuickReply", "Detener promociones", null, null), new TemplateButtonInput("Url", "Ver catálogo", "https://ejemplo.co/catalogo", null), new TemplateButtonInput("PhoneNumber", "Llamar", null, "+573001234567")],
            new Dictionary<string, string> { ["first_name"] = "Ana", ["product"] = "Silla Nova", ["discount"] = "20 %" });

    private static JsonElement Element(System.Text.Json.Nodes.JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    [Fact]
    public void VariablesComeOutInOrderOfAppearanceWithoutRepeats() =>
        Assert.Equal(["first_name", "product", "discount"], TemplateComponentsBuilder.VariablesOf(Draft().Body));

    [Fact]
    public void ThePayloadCarriesNameLanguageCategoryNamedFormatAndComponents()
    {
        var payload = Element(TemplateComponentsBuilder.CreatePayload(Draft()));

        Assert.Equal(("promo_octubre", "es", "MARKETING", "NAMED"), (
            payload.GetProperty("name").GetString(), payload.GetProperty("language").GetString(), payload.GetProperty("category").GetString(), payload.GetProperty("parameter_format").GetString()));
        Assert.Equal(["HEADER", "BODY", "FOOTER", "BUTTONS"], payload.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("type").GetString()));
        Assert.False(payload.TryGetProperty("allow_category_change", out _));
    }

    [Fact]
    public void TheBodyCarriesOneNamedExamplePerVariable()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft())).EnumerateArray().ToArray();
        var body = components.Single(component => component.GetProperty("type").GetString() == "BODY");

        Assert.Equal(Draft().Body, body.GetProperty("text").GetString());
        var examples = body.GetProperty("example").GetProperty("body_text_named_params").EnumerateArray().ToArray();
        Assert.Equal(["first_name|Ana", "product|Silla Nova", "discount|20 %"], examples.Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Fact]
    public void ABodyWithoutVariablesCarriesNoExample()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft(body: "Hola, ya abrimos.", header: null, footer: null, buttons: []))).EnumerateArray().ToArray();

        var body = Assert.Single(components);
        Assert.Equal("BODY", body.GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("example", out _));
    }

    [Fact]
    public void TheTextHeaderAndFooterAreOptional()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft(header: null, footer: null, buttons: []))).EnumerateArray().ToArray();
        Assert.Equal(["BODY"], components.Select(component => component.GetProperty("type").GetString()));

        var withHeader = Element(TemplateComponentsBuilder.Components(Draft(buttons: []))).EnumerateArray().ToArray();
        Assert.Equal(("HEADER", "TEXT", "Oferta de octubre"), (withHeader[0].GetProperty("type").GetString(), withHeader[0].GetProperty("format").GetString(), withHeader[0].GetProperty("text").GetString()));
        Assert.Equal(("FOOTER", "Responde DETENER para no recibir promociones."), (withHeader[2].GetProperty("type").GetString(), withHeader[2].GetProperty("text").GetString()));
    }

    [Fact]
    public void EachButtonTypeMapsToMetasNameAndCarriesOnlyItsField()
    {
        var buttons = Element(TemplateComponentsBuilder.Components(Draft())).EnumerateArray().Single(component => component.GetProperty("type").GetString() == "BUTTONS")
            .GetProperty("buttons").EnumerateArray().ToArray();

        Assert.Equal(["QUICK_REPLY", "URL", "PHONE_NUMBER"], buttons.Select(button => button.GetProperty("type").GetString()));
        Assert.Equal("Detener promociones", buttons[0].GetProperty("text").GetString());
        Assert.False(buttons[0].TryGetProperty("url", out _));
        Assert.Equal("https://ejemplo.co/catalogo", buttons[1].GetProperty("url").GetString());
        Assert.Equal("+573001234567", buttons[2].GetProperty("phone_number").GetString());
        Assert.False(buttons[2].TryGetProperty("url", out _));
    }

    [Fact]
    public void ADocumentHeaderReplacesTheTextHeaderAndCarriesTheHandle()
    {
        var components = Element(TemplateComponentsBuilder.Components(Draft(buttons: []), documentHeaderHandle: "4::handle")).EnumerateArray().ToArray();

        var header = components[0];
        Assert.Equal(("HEADER", "DOCUMENT"), (header.GetProperty("type").GetString(), header.GetProperty("format").GetString()));
        Assert.False(header.TryGetProperty("text", out _));
        Assert.Equal(["4::handle"], header.GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
    }
}
