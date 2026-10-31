using System.Globalization;
using System.Text;
using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.5 y plantillas editables: la definición de la plantilla de cotización. Texto base del spec con las claves
/// nuevas y sin pie, encabezado de documento con el handle, ejemplos de <see cref="SystemTemplateVariables"/> en orden de aparición,
/// <c>IsAdoptable</c> mira la forma y no el texto, y las versiones del nombre (<c>qep_cotizacion</c>, <c>qep_cotizacion_v2</c>…) que usa el
/// reintento.</summary>
public sealed class QuotationTemplateDefinitionTests
{
    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal((MessageTemplateName.QuotationName, "es", "UTILITY"), (QuotationTemplateDefinition.Name, QuotationTemplateDefinition.Language, QuotationTemplateDefinition.Category));
        Assert.Equal(
            ["nombre_contacto", "numero_cotizacion", "valor_total", "vigencia"], TemplateComponentsBuilder.VariablesOf(QuotationTemplateDefinition.BaseBody));
        Assert.Equal(
            SystemTemplateVariables.Quotation.ToDictionary(variable => variable.Key, variable => variable.Example), QuotationTemplateDefinition.Examples);
    }

    [Fact]
    public void TheBaseTextIsTheSpecsAndNewTemplatesHaveNoFooter()
    {
        Assert.Equal(
            "¡Hola, {{nombre_contacto}}! Te compartimos en este mensaje la cotización *{{numero_cotizacion}}*, por un valor total de *{{valor_total}}*. Esta cotización estará vigente hasta el *{{vigencia}}*. Si tienes alguna pregunta, responde este mensaje y con gusto te ayudamos.",
            QuotationTemplateDefinition.BaseBody);
        Assert.Null(QuotationTemplateDefinition.Draft("qep_cotizacion", QuotationTemplateDefinition.BaseBody).Footer);
        var components = QuotationTemplateDefinition.Payload("handle", "qep_cotizacion", "Hola {{nombre_contacto}}, gracias.")["components"]!.AsArray();
        Assert.DoesNotContain(components, component => component!["type"]!.GetValue<string>() == "FOOTER");
    }

    [Fact]
    public void ThePayloadCarriesADocumentHeaderWithTheHandleAndTheBodyWithExamplesOnly()
    {
        using var payload = JsonDocument.Parse(
            QuotationTemplateDefinition.Payload("4::handle", QuotationTemplateDefinition.Name, QuotationTemplateDefinition.BaseBody).ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_cotizacion", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(), root.GetProperty("parameter_format").GetString()));
        var components = root.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["HEADER", "BODY"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("DOCUMENT", components[0].GetProperty("format").GetString());
        Assert.Equal(["4::handle"], components[0].GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(QuotationTemplateDefinition.BaseBody, components[1].GetProperty("text").GetString());
        Assert.Equal(["nombre_contacto|María González", "numero_cotizacion|COT-000123", "valor_total|2.450.000 COP", "vigencia|30 de septiembre de 2026"],
            components[1].GetProperty("example").GetProperty("body_text_named_params").EnumerateArray().Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Fact]
    public void ThePayloadTakesTheVersionedNameAndKeepsTheSameText()
    {
        var first = QuotationTemplateDefinition.Payload("4::handle", QuotationTemplateDefinition.Name, QuotationTemplateDefinition.BaseBody);
        var third = QuotationTemplateDefinition.Payload("4::handle", "qep_cotizacion_v3", QuotationTemplateDefinition.BaseBody);

        Assert.Equal("qep_cotizacion_v3", third["name"]!.GetValue<string>());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(first["components"], third["components"]));
        Assert.Equal("qep_cotizacion_v3", QuotationTemplateDefinition.Draft("qep_cotizacion_v3", QuotationTemplateDefinition.BaseBody).Name);
    }

    [Fact]
    public void IsAdoptableAcceptsItsOwnComponentsAsMetaWouldReturnThem()
    {
        var components = QuotationTemplateDefinition.Payload("4::handle", QuotationTemplateDefinition.Name, QuotationTemplateDefinition.BaseBody)["components"]!.ToJsonString();

        Assert.True(QuotationTemplateDefinition.IsAdoptable(components));
    }

    // El cuerpo y el pie viejos siguen siendo adoptables: el texto ya no decide.
    [Theory]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, gracias."},{"type":"FOOTER","text":"Este mensaje fue generado automáticamente."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{otra}}, gracias."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("""[{"type":"HEADER","format":42},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("no es json", false)]
    public void AdoptionLooksAtTheShapeNotTheText(string components, bool expected) =>
        Assert.Equal(expected, QuotationTemplateDefinition.IsAdoptable(components));

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
    public void TheExamplePdfIsSmall() => Assert.InRange(QuotationTemplateDefinition.ExamplePdf().Length, 300, 2000);

    [Fact]
    public void MinimalPdfEscapesParenthesesAndBackslashes()
    {
        var text = Encoding.ASCII.GetString(MinimalPdf.Build(@"a (b) \ c"));

        Assert.Contains(@"(a \(b\) \\ c) Tj", text, StringComparison.Ordinal);
    }
}
