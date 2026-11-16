using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Soporte fase 4b (H10, owner 2026-11-16 Q-1): la plantilla «Respuesta formal», un tipo aparte de «Aviso de respuesta» porque
/// Meta no admite un encabezado opcional. Lleva el PDF como encabezado de documento (el camino de la de pedido), las variables de Soporte,
/// un texto base tuteando que no empieza ni termina con variable y un nombre reservado por versiones (<c>qep_soporte_formal</c>…).</summary>
public sealed class SupportFormalReplyTemplateDefinitionTests
{
    [Fact]
    public void NameVersionsAndPrefixAreReserved()
    {
        Assert.Equal(("qep_soporte_formal", "qep_soporte_formal_"), (MessageTemplateName.SupportFormalReplyName, MessageTemplateName.SupportFormalReplyPrefix));
        Assert.Equal(
            (MessageTemplateName.SupportFormalReplyName, "es", "UTILITY"),
            (SupportFormalReplyTemplateDefinition.Name, SupportFormalReplyTemplateDefinition.Language, SupportFormalReplyTemplateDefinition.Category));
        Assert.Equal("qep_soporte_formal", SupportFormalReplyTemplateDefinition.NameFor(1));
        Assert.Equal("qep_soporte_formal_v2", SupportFormalReplyTemplateDefinition.NameFor(2));
        Assert.Equal(2, SupportFormalReplyTemplateDefinition.VersionOf("qep_soporte_formal_v2"));
        Assert.Equal(1, SupportFormalReplyTemplateDefinition.VersionOf("qep_soporte_formal"));
        Assert.Null(SupportFormalReplyTemplateDefinition.VersionOf("qep_soporte_formal_v1"));
        Assert.Null(SupportFormalReplyTemplateDefinition.VersionOf("qep_soporte_respuesta_v2"));
        Assert.Null(SupportReplyTemplateDefinition.VersionOf("qep_soporte_formal"));
        Assert.True(MessageTemplateName.IsReserved("qep_soporte_formal"));
        Assert.True(MessageTemplateName.IsReserved("qep_soporte_formal_x"));
        Assert.False(MessageTemplateName.IsReserved("qep_soporte_formales"));

        var error = Assert.Throws<MessagingDomainException>(() => MessageTemplate.Pending(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "qep_soporte_formal_v3", "es", "UTILITY", "NAMED", "PENDING",
            """[{"type":"BODY","text":"Hola"}]""", MessageTemplateKind.Free, null, DateTimeOffset.UnixEpoch));
        Assert.Equal(MessagingErrorCodes.TemplateNameReserved, error.Code);
    }

    [Fact]
    public void TheBaseBodyNeitherStartsNorEndsWithAVariable()
    {
        Assert.Equal(
            "Hola {{nombre_contacto}}, te enviamos la respuesta formal a tu caso {{radicado}}: {{respuesta}}. Si tienes dudas, responde este mensaje.",
            SupportFormalReplyTemplateDefinition.BaseBody);
        var body = SupportFormalReplyTemplateDefinition.BaseBody.Trim();

        Assert.DoesNotMatch(@"^\{\{[a-z0-9_]+\}\}", body);
        Assert.DoesNotMatch(@"\{\{[a-z0-9_]+\}\}[.!?]?$", body);
        Assert.Equal(["nombre_contacto", "radicado", "respuesta"], TemplateComponentsBuilder.VariablesOf(body));
    }

    // Meta rechaza la plantilla si un ejemplo lleva $, # o % (README § Plantilla de WhatsApp).
    [Fact]
    public void ExamplesHaveNoForbiddenCharacters()
    {
        Assert.Equal(
            SystemTemplateVariables.SupportFormalReply.ToDictionary(variable => variable.Key, variable => variable.Example),
            SupportFormalReplyTemplateDefinition.Examples);
        Assert.All(SupportFormalReplyTemplateDefinition.Examples.Values, example => Assert.DoesNotMatch("[$#%]", example));
        Assert.Equal(("respuesta-formal-ejemplo.pdf", "application/pdf"), (
            SupportFormalReplyTemplateDefinition.ExampleFileName, SupportFormalReplyTemplateDefinition.ExampleMimeType));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(SupportFormalReplyTemplateDefinition.ExamplePdf()), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePayloadCarriesADocumentHeaderWithTheHandle()
    {
        using var payload = JsonDocument.Parse(SupportFormalReplyTemplateDefinition
            .Payload("4::handle", SupportFormalReplyTemplateDefinition.Name, SupportFormalReplyTemplateDefinition.BaseBody).ToJsonString());
        var components = payload.RootElement.GetProperty("components").EnumerateArray().ToArray();

        Assert.Equal(["HEADER", "BODY"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal("DOCUMENT", components[0].GetProperty("format").GetString());
        Assert.Equal("4::handle", components[0].GetProperty("example").GetProperty("header_handle")[0].GetString());
    }

    [Theory]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}: {{respuesta}}."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, caso {{radicado}}."}]""", true)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, pedido {{numero_pedido}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{fullname}}, caso {{order_number}}."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Soporte"},{"type":"BODY","text":"Hola {{nombre_contacto}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"   "}]""", false)]
    [InlineData("no es json", false)]
    public void OnlyADocumentHeaderTemplateIsAdoptable(string components, bool expected) =>
        Assert.Equal(expected, SupportFormalReplyTemplateDefinition.IsAdoptable(components));

    [Fact]
    public void ItsOwnComponentsAreAdoptable()
    {
        var components = SupportFormalReplyTemplateDefinition
            .Payload("4::handle", SupportFormalReplyTemplateDefinition.Name, SupportFormalReplyTemplateDefinition.BaseBody)["components"]!
            .ToJsonString();

        Assert.True(SupportFormalReplyTemplateDefinition.IsAdoptable(components));
    }
}
