using System.Globalization;
using System.Text;
using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.5: la única fuente del texto de la plantilla de cotización. Mismo cuerpo que la de Zenvia, encabezado de
/// documento con el handle, cuatro variables con ejemplo, <c>Matches</c> reconoce su propio resultado y rechaza otro cuerpo, y las versiones
/// del nombre (<c>qep_cotizacion</c>, <c>qep_cotizacion_v2</c>…) que usa el reintento.</summary>
public sealed class QuotationTemplateDefinitionTests
{
    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal((MessageTemplateName.QuotationName, "es", "UTILITY"), (QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, QuotationTemplateDefinition.Category));
        Assert.Equal(["fullname", "order_number", "total", "valid_until"], QuotationTemplateDefinition.Variables);
        Assert.Equal(QuotationTemplateDefinition.Variables, TemplateComponentsBuilder.VariablesOf(QuotationTemplateDefinition.Body));
        Assert.Equal(QuotationTemplateDefinition.Variables.Order(StringComparer.Ordinal), QuotationTemplateDefinition.Examples.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ThePayloadCarriesADocumentHeaderWithTheHandleTheBodyWithExamplesAndTheFooter()
    {
        using var payload = JsonDocument.Parse(QuotationTemplateDefinition.Payload("4::handle", QuotationTemplateDefinition.Name).ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_cotizacion", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(), root.GetProperty("parameter_format").GetString()));
        var components = root.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["HEADER", "BODY", "FOOTER"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("DOCUMENT", components[0].GetProperty("format").GetString());
        Assert.Equal(["4::handle"], components[0].GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(QuotationTemplateDefinition.Body, components[1].GetProperty("text").GetString());
        Assert.Equal(["fullname|Juan Perez", "order_number|COT-000123", "total|2.450.000 COP", "valid_until|30 de septiembre de 2026"],
            components[1].GetProperty("example").GetProperty("body_text_named_params").EnumerateArray().Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
        Assert.Equal(QuotationTemplateDefinition.Footer, components[2].GetProperty("text").GetString());
    }

    [Fact]
    public void ThePayloadTakesTheVersionedNameAndKeepsTheSameText()
    {
        var first = QuotationTemplateDefinition.Payload("4::handle", QuotationTemplateDefinition.Name);
        var third = QuotationTemplateDefinition.Payload("4::handle", "qep_cotizacion_v3");

        Assert.Equal("qep_cotizacion_v3", third["name"]!.GetValue<string>());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(first["components"], third["components"]));
        Assert.Equal("qep_cotizacion_v3", QuotationTemplateDefinition.Draft("qep_cotizacion_v3").Name);
    }

    [Fact]
    public void MatchesAcceptsItsOwnComponentsAsMetaWouldReturnThem()
    {
        // Meta devuelve el encabezado con su example y el cuerpo con el suyo; Matches mira cuerpo, pie, formato y variables.
        var components = QuotationTemplateDefinition.Payload("4::handle", QuotationTemplateDefinition.Name)["components"]!.ToJsonString();

        Assert.True(QuotationTemplateDefinition.Matches(components));
    }

    [Theory]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Otro cuerpo {{fullname}} {{order_number}} {{total}} {{valid_until}}"},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("""[{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."}]""")]
    [InlineData("""[{"type":"HEADER","format":42},{"type":"BODY","text":"¡Hola, {{fullname}}! Adjuntamos la cotización *{{order_number}}* por *{{total}}*, vigente hasta el *{{valid_until}}*."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""")]
    [InlineData("no es json")]
    public void MatchesRejectsAnotherBodyHeaderFormatOrFooter(string components) => Assert.False(QuotationTemplateDefinition.Matches(components));

    [Theory]
    [InlineData(1, "qep_cotizacion")]
    [InlineData(2, "qep_cotizacion_v2")]
    [InlineData(10, "qep_cotizacion_v10")]
    public void NameForGivesTheBaseNameToVersionOneAndASuffixToTheRest(int version, string expected)
    {
        Assert.Equal(expected, QuotationTemplateDefinition.NameFor(version));
        Assert.Equal(version, QuotationTemplateDefinition.VersionOf(expected));
        Assert.True(MessageTemplateName.IsReserved(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NameForRejectsAVersionBelowOne(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QuotationTemplateDefinition.NameFor(version));

    [Theory]
    [InlineData("qep_cotizacion_v1")]
    [InlineData("qep_cotizacion_v0")]
    [InlineData("qep_cotizacion_vx")]
    [InlineData("qep_cotizacion_v02")]
    [InlineData("qep_cotizacion_v")]
    [InlineData("qep_cotizacion_v2x")]
    [InlineData("qep_cotizacion_v99999999999")]
    [InlineData("qep_cotizacion_2")]
    [InlineData("qep_cotizaciones")]
    [InlineData("promo_octubre")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionOfIsNullForAnythingThatIsNotAVersionOfTheQuotationTemplate(string? name) => Assert.Null(QuotationTemplateDefinition.VersionOf(name));

    [Fact]
    public void TheExamplePdfIsAValidMinimalDocument()
    {
        var bytes = QuotationTemplateDefinition.ExamplePdf();
        var text = Encoding.ASCII.GetString(bytes);

        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);
        Assert.Contains("(Cotizacion de ejemplo - QEP)", text, StringComparison.Ordinal);
        // El startxref apunta exactamente a la tabla xref.
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length)..].Split('\n')[0], CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring(startxref, 4));
        // Y cada offset de la tabla apunta a «N 0 obj».
        var xref = text[startxref..];
        var entries = xref.Split('\n').Skip(3).Take(5).Select(line => int.Parse(line[..10], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(5, entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            Assert.StartsWith($"{index + 1} 0 obj", text[entries[index]..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ParametersFollowTheVariablesOrder()
    {
        var parameters = QuotationTemplateDefinition.ParametersFor("Juan Perez", "COT-000123", "$ 2.450.000", "30 de septiembre de 2026");

        Assert.Equal(["fullname", "order_number", "total", "valid_until"], parameters.Select(parameter => parameter.Key));
        Assert.Equal(["Juan Perez", "COT-000123", "$ 2.450.000", "30 de septiembre de 2026"], parameters.Select(parameter => parameter.Value));
    }

    [Fact]
    public void RenderFillsTheFourVariablesAndAppendsTheFooter()
    {
        var parameters = QuotationTemplateDefinition.ParametersFor("Juan Perez", "COT-000123", "$ 2.450.000", "30 de septiembre de 2026");

        Assert.Equal(
            "¡Hola, Juan Perez! Adjuntamos la cotización *COT-000123* por *$ 2.450.000*, vigente hasta el *30 de septiembre de 2026*.\n\nEste mensaje fue generado automáticamente.",
            QuotationTemplateDefinition.Render(parameters));
    }

    [Fact]
    public void TheExamplePdfIsSmall() => Assert.InRange(QuotationTemplateDefinition.ExamplePdf().Length, 300, 2000);

    [Fact]
    public void MinimalPdfEscapesParenthesesAndBackslashes()
    {
        var text = Encoding.ASCII.GetString(MinimalPdf.Build(@"a (b) \ c"));

        Assert.Contains(@"(a \(b\) \\ c) Tj", text, StringComparison.Ordinal);
    }
}
