using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-21: la definición de la plantilla de factura, hermana de la de pedido. Texto base con las variables de
/// <see cref="SystemTemplateVariables.Invoice"/>, encabezado de documento con el handle, <c>IsAdoptable</c> mira la forma y no el texto, y
/// las versiones del nombre (<c>qep_factura</c>, <c>qep_factura_v2</c>…) que usa el reintento.</summary>
public sealed class InvoiceTemplateDefinitionTests
{
    [Fact]
    public void NameForVersionOneIsTheBaseName() => Assert.Equal("qep_factura", InvoiceTemplateDefinition.NameFor(1));

    [Fact]
    public void NameForLaterVersionsAreSuffixed()
    {
        Assert.Equal("qep_factura_v2", InvoiceTemplateDefinition.NameFor(2));
        Assert.Equal(10, InvoiceTemplateDefinition.VersionOf(InvoiceTemplateDefinition.NameFor(10)));
        Assert.Equal(1, InvoiceTemplateDefinition.VersionOf("qep_factura"));
        Assert.True(MessageTemplateName.IsReserved("qep_factura_v2"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NameForRejectsAVersionBelowOne(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InvoiceTemplateDefinition.NameFor(version));

    [Fact]
    public void VersionOfRoundTripsAndRejectsLookalikes()
    {
        Assert.Null(InvoiceTemplateDefinition.VersionOf("qep_factura_v02"));
        Assert.Null(InvoiceTemplateDefinition.VersionOf("qep_factura_v1"));
        Assert.Null(InvoiceTemplateDefinition.VersionOf("qep_factura_v"));
        Assert.Null(InvoiceTemplateDefinition.VersionOf("qep_facturas"));
        Assert.Null(InvoiceTemplateDefinition.VersionOf("qep_pedido_v2"));
        Assert.Null(InvoiceTemplateDefinition.VersionOf(""));
        Assert.Null(InvoiceTemplateDefinition.VersionOf(null));
    }

    [Fact]
    public void TheBaseBodyUsesOnlyInvoiceVariables()
    {
        Assert.Equal((MessageTemplateName.InvoiceName, "es", "UTILITY"), (InvoiceTemplateDefinition.Name, InvoiceTemplateDefinition.Language, InvoiceTemplateDefinition.Category));
        Assert.Equal(
            "Hola {{nombre_contacto}}, te enviamos la factura {{numero_factura}} por un total de {{valor_total}}. Adjuntamos el documento. ¡Gracias por tu compra!",
            InvoiceTemplateDefinition.BaseBody);
        Assert.Equal(["nombre_contacto", "numero_factura", "valor_total"], TemplateComponentsBuilder.VariablesOf(InvoiceTemplateDefinition.BaseBody));
        Assert.Null(TemplateBodyRules.Check(InvoiceTemplateDefinition.BaseBody, SystemTemplateVariables.KeysOf(SystemTemplateVariables.Invoice)));
        Assert.Equal(["nombre_contacto", "asesor", "empresa", "saludo", "numero_factura", "valor_total"], SystemTemplateVariables.Invoice.Select(v => v.Key));
        Assert.Null(InvoiceTemplateDefinition.Draft("qep_factura", InvoiceTemplateDefinition.BaseBody).Footer);
    }

    [Fact]
    public void ThePayloadCarriesADocumentHeaderWithTheHandleAndTheBodyWithExamplesOnly()
    {
        using var payload = JsonDocument.Parse(
            InvoiceTemplateDefinition.Payload("4::handle", InvoiceTemplateDefinition.Name, InvoiceTemplateDefinition.BaseBody).ToJsonString());
        var components = payload.RootElement.GetProperty("components").EnumerateArray().ToArray();

        Assert.Equal(["HEADER", "BODY"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("DOCUMENT", components[0].GetProperty("format").GetString());
        Assert.Equal(["4::handle"], components[0].GetProperty("example").GetProperty("header_handle").EnumerateArray().Select(handle => handle.GetString()));
        Assert.Equal(
            ["nombre_contacto|María González", "numero_factura|FV-1-000123", "valor_total|2.450.000 COP"],
            components[1].GetProperty("example").GetProperty("body_text_named_params").EnumerateArray()
                .Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Fact]
    public void IsAdoptableAcceptsItsOwnComponentsAsMetaWouldReturnThem() =>
        Assert.True(InvoiceTemplateDefinition.IsAdoptable(
            InvoiceTemplateDefinition.Payload("4::handle", InvoiceTemplateDefinition.Name, InvoiceTemplateDefinition.BaseBody)["components"]!.ToJsonString()));

    [Theory]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, factura {{numero_factura}}."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, vence {{vigencia}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("no es json", false)]
    public void IsAdoptableAcceptsTheDocumentHeaderShapeAndRejectsVigencia(string components, bool expected) =>
        Assert.Equal(expected, InvoiceTemplateDefinition.IsAdoptable(components));

    [Fact]
    public void TheOrderDefinitionRejectsAnInvoiceBody()
    {
        var invoice = InvoiceTemplateDefinition.Payload("4::handle", InvoiceTemplateDefinition.Name, InvoiceTemplateDefinition.BaseBody)["components"]!.ToJsonString();

        Assert.False(OrderTemplateDefinition.IsAdoptable(invoice));
        Assert.False(InvoiceTemplateDefinition.IsAdoptable(
            OrderTemplateDefinition.Payload("4::handle", OrderTemplateDefinition.Name, OrderTemplateDefinition.BaseBody)["components"]!.ToJsonString()));
    }
}
