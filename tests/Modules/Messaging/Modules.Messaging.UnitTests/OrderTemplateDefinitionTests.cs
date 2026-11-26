using System.Text;
using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09 D1 y D2: la definición de la plantilla de pedido, calcada de la de cotización. Texto base sin vigencia,
/// encabezado de documento con el handle, ejemplos de <see cref="SystemTemplateVariables.Order"/>, <c>IsAdoptable</c> mira la forma y no
/// el texto, y las versiones del nombre (<c>qep_pedido</c>, <c>qep_pedido_v2</c>…) que usa el reintento.</summary>
public sealed class OrderTemplateDefinitionTests
{
    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal((MessageTemplateName.OrderName, "es", "UTILITY"), (OrderTemplateDefinition.Name, OrderTemplateDefinition.Language, OrderTemplateDefinition.Category));
        Assert.Equal(["nombre_contacto", "numero_pedido", "valor_total"], TemplateComponentsBuilder.VariablesOf(OrderTemplateDefinition.BaseBody));
        Assert.Equal(SystemTemplateVariables.Order.ToDictionary(variable => variable.Key, variable => variable.Example), OrderTemplateDefinition.Examples);
        Assert.Equal("pedido-ejemplo.pdf", OrderTemplateDefinition.ExampleFileName);
    }

    [Fact]
    public void TheBaseTextIsTheSpecsAndNewTemplatesHaveNoFooter()
    {
        Assert.Equal(
            "Hola {{nombre_contacto}}, te enviamos el pedido {{numero_pedido}} por un total de {{valor_total}}. Adjuntamos el documento. ¡Gracias por tu compra!",
            OrderTemplateDefinition.BaseBody);
        Assert.Null(OrderTemplateDefinition.Draft("qep_pedido", OrderTemplateDefinition.BaseBody).Footer);
        var components = OrderTemplateDefinition.Payload("handle", "qep_pedido", "Hola {{nombre_contacto}}, gracias.")["components"]!.AsArray();
        Assert.DoesNotContain(components, component => component!["type"]!.GetValue<string>() == "FOOTER");
    }

    [Fact]
    public void ThePayloadCarriesADocumentHeaderWithTheHandleAndTheBodyWithExamplesOnly()
    {
        using var payload = JsonDocument.Parse(
            OrderTemplateDefinition.Payload("4::handle", OrderTemplateDefinition.Name, OrderTemplateDefinition.BaseBody).ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_pedido", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(), root.GetProperty("parameter_format").GetString()));
        var components = root.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["HEADER", "BODY"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("DOCUMENT", components[0].GetProperty("format").GetString());
        Assert.Equal(["4::handle"], components[0].GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(OrderTemplateDefinition.BaseBody, components[1].GetProperty("text").GetString());
        Assert.Equal(["nombre_contacto|María González", "numero_pedido|PED-000123", "valor_total|2.450.000 COP"],
            components[1].GetProperty("example").GetProperty("body_text_named_params").EnumerateArray().Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Fact]
    public void IsAdoptableAcceptsItsOwnComponentsAsMetaWouldReturnThem()
    {
        var components = OrderTemplateDefinition.Payload("4::handle", OrderTemplateDefinition.Name, OrderTemplateDefinition.BaseBody)["components"]!.ToJsonString();

        Assert.True(OrderTemplateDefinition.IsAdoptable(components));
    }

    // Las claves viejas (order_number) también cuentan: LegacyKeys las traduce al enviar.
    [Theory]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, pedido {{numero_pedido}}."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, pedido {{order_number}}."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, vence {{vigencia}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("no es json", false)]
    public void AdoptionLooksAtTheShapeNotTheText(string components, bool expected) =>
        Assert.Equal(expected, OrderTemplateDefinition.IsAdoptable(components));

    [Theory]
    [InlineData(1, "qep_pedido")]
    [InlineData(2, "qep_pedido_v2")]
    [InlineData(10, "qep_pedido_v10")]
    public void NameForGivesTheBaseNameToVersionOneAndASuffixToTheRest(int version, string expected)
    {
        Assert.Equal(expected, OrderTemplateDefinition.NameFor(version));
        Assert.Equal(version, OrderTemplateDefinition.VersionOf(expected));
        Assert.True(MessageTemplateName.IsReserved(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NameForRejectsAVersionBelowOne(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderTemplateDefinition.NameFor(version));

    [Theory]
    [InlineData("qep_pedido_v1")]
    [InlineData("qep_pedido_v0")]
    [InlineData("qep_pedido_v02")]
    [InlineData("qep_pedido_v")]
    [InlineData("qep_pedidos")]
    [InlineData("qep_cotizacion_v2")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionOfIsNullForAnythingThatIsNotAVersionOfTheOrderTemplate(string? name) => Assert.Null(OrderTemplateDefinition.VersionOf(name));

    [Fact]
    public void TheExamplePdfNamesTheOrder()
    {
        var text = Encoding.ASCII.GetString(OrderTemplateDefinition.ExamplePdf());

        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.Contains("(Pedido de ejemplo - QEP)", text, StringComparison.Ordinal);
    }
}
