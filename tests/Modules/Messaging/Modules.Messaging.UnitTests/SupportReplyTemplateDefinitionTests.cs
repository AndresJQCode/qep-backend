using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Soporte fase 2: la definición de la plantilla «Aviso de respuesta», calcada de la de pedido pero sin encabezado (el PDF formal
/// es de la fase 4). Texto base tuteando, variables fijas más <c>radicado</c> y <c>respuesta</c>, ejemplos que Meta acepta, nombre
/// reservado con sus versiones (<c>qep_soporte_respuesta</c>, <c>qep_soporte_respuesta_v2</c>…) e <c>IsAdoptable</c> que mira la forma.</summary>
public sealed class SupportReplyTemplateDefinitionTests
{
    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal(("qep_soporte_respuesta", "qep_soporte_respuesta_"), (MessageTemplateName.SupportReplyName, MessageTemplateName.SupportReplyPrefix));
        Assert.Equal(
            (MessageTemplateName.SupportReplyName, "es", "UTILITY"),
            (SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.Language, SupportReplyTemplateDefinition.Category));
        Assert.Equal(["nombre_contacto", "radicado", "respuesta"], TemplateComponentsBuilder.VariablesOf(SupportReplyTemplateDefinition.BaseBody));
        Assert.Equal(
            SystemTemplateVariables.SupportReply.ToDictionary(variable => variable.Key, variable => variable.Example),
            SupportReplyTemplateDefinition.Examples);
    }

    [Fact]
    public void TheVariablesAreTheFixedOnesPlusTheCaseNumberAndTheReply()
    {
        Assert.Equal(
            ["nombre_contacto", "asesor", "empresa", "saludo", "radicado", "respuesta"],
            SystemTemplateVariables.SupportReply.Select(variable => variable.Key));
        var radicado = SystemTemplateVariables.SupportReply.Single(variable => variable.Key == "radicado");
        var respuesta = SystemTemplateVariables.SupportReply.Single(variable => variable.Key == "respuesta");
        Assert.Equal(("Radicado del caso", "SOP-000127"), (radicado.Label, radicado.Example));
        Assert.Equal(("Respuesta del asesor", "Revisamos tu solicitud y ya quedó resuelta."), (respuesta.Label, respuesta.Example));
    }

    [Fact]
    public void TheBaseTextTutea()
    {
        Assert.Equal(
            "Hola {{nombre_contacto}}, te escribimos sobre tu caso {{radicado}}: {{respuesta}}. Si tienes dudas, responde este mensaje.",
            SupportReplyTemplateDefinition.BaseBody);
        Assert.Null(SupportReplyTemplateDefinition.Draft(SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.BaseBody).Footer);
    }

    // Meta rechaza un cuerpo que empieza o termina con una variable.
    [Fact]
    public void TheBaseBodyNeitherStartsNorEndsWithAVariable()
    {
        var body = SupportReplyTemplateDefinition.BaseBody.Trim();

        Assert.DoesNotMatch(@"^\{\{[a-z0-9_]+\}\}", body);
        Assert.DoesNotMatch(@"\{\{[a-z0-9_]+\}\}[.!?]?$", body);
    }

    // Meta rechaza la plantilla si un ejemplo lleva $, # o % (README § Plantilla de WhatsApp).
    [Fact]
    public void NoExampleCarriesACharacterMetaRejects() =>
        Assert.All(SupportReplyTemplateDefinition.Examples.Values, example => Assert.DoesNotMatch("[$#%]", example));

    [Fact]
    public void ThePayloadHasOnlyTheBodyWithNamedExamples()
    {
        using var payload = JsonDocument.Parse(
            SupportReplyTemplateDefinition.Payload(SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.BaseBody).ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_soporte_respuesta", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(),
            root.GetProperty("parameter_format").GetString()));
        var component = Assert.Single(root.GetProperty("components").EnumerateArray());
        Assert.Equal("BODY", component.GetProperty("type").GetString());
        Assert.Equal(SupportReplyTemplateDefinition.BaseBody, component.GetProperty("text").GetString());
        Assert.Equal(
            ["nombre_contacto|María González", "radicado|SOP-000127", "respuesta|Revisamos tu solicitud y ya quedó resuelta."],
            component.GetProperty("example").GetProperty("body_text_named_params").EnumerateArray()
                .Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Fact]
    public void IsAdoptableAcceptsItsOwnComponentsAsMetaWouldReturnThem()
    {
        var components = SupportReplyTemplateDefinition.Payload(SupportReplyTemplateDefinition.Name, SupportReplyTemplateDefinition.BaseBody)["components"]!
            .ToJsonString();

        Assert.True(SupportReplyTemplateDefinition.IsAdoptable(components));
    }

    [Theory]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}: {{respuesta}}"}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, gracias por escribirnos."}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, pedido {{numero_pedido}}."}]""", false)]
    // De las claves viejas sólo fullname: el envío no sabe llenar order_number, total ni valid_until.
    [InlineData("""[{"type":"BODY","text":"Hola {{fullname}}, caso {{radicado}}: {{respuesta}}."}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{fullname}}, caso {{order_number}}: {{respuesta}}."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, vence {{valid_until}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Soporte"},{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"   "}]""", false)]
    [InlineData("no es json", false)]
    public void AdoptionLooksAtTheShapeNotTheText(string components, bool expected) =>
        Assert.Equal(expected, SupportReplyTemplateDefinition.IsAdoptable(components));

    [Theory]
    [InlineData(1, "qep_soporte_respuesta")]
    [InlineData(2, "qep_soporte_respuesta_v2")]
    [InlineData(10, "qep_soporte_respuesta_v10")]
    public void NameForGivesTheBaseNameToVersionOneAndASuffixToTheRest(int version, string expected)
    {
        Assert.Equal(expected, SupportReplyTemplateDefinition.NameFor(version));
        Assert.Equal(version, SupportReplyTemplateDefinition.VersionOf(expected));
        Assert.True(MessageTemplateName.IsReserved(expected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NameForRejectsAVersionBelowOne(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SupportReplyTemplateDefinition.NameFor(version));

    [Theory]
    [InlineData("qep_soporte_respuesta_v1")]
    [InlineData("qep_soporte_respuesta_v0")]
    [InlineData("qep_soporte_respuesta_v02")]
    [InlineData("qep_soporte_respuesta_v")]
    [InlineData("qep_soporte_respuestas")]
    [InlineData("qep_pedido_v2")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionOfIsNullForAnythingThatIsNotAVersionOfTheSupportReplyTemplate(string? name) =>
        Assert.Null(SupportReplyTemplateDefinition.VersionOf(name));

    // El prefijo reservado cubre cualquier cosa debajo, no sólo las versiones: una libre no puede tomar «qep_soporte_respuesta_x».
    [Theory]
    [InlineData("qep_soporte_respuesta", true)]
    [InlineData("qep_soporte_respuesta_x", true)]
    [InlineData("qep_soporte_respuestas", false)]
    [InlineData("qep_soporte", false)]
    public void TheNameAndThePrefixAreReserved(string name, bool reserved) =>
        Assert.Equal(reserved, MessageTemplateName.IsReserved(name));

    [Fact]
    public void AFreeTemplateCannotTakeTheReservedName()
    {
        var error = Assert.Throws<MessagingDomainException>(() => MessageTemplate.Pending(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "qep_soporte_respuesta_v3", "es", "UTILITY", "NAMED", "PENDING",
            """[{"type":"BODY","text":"Hola"}]""", MessageTemplateKind.Free, null, DateTimeOffset.UnixEpoch));

        Assert.Equal(MessagingErrorCodes.TemplateNameReserved, error.Code);
    }

    [Fact]
    public void TheKindIsStoredByNameAtTheEnd() =>
        Assert.Equal(
            ["Free", "Quotation", "External", "BillingReminder", "BillingOverdue", "Order", "SupportReply", "SupportFormalReply", "SupportSurvey",
                "WholesaleInvitationNotice", "Invoice", "AppointmentReminder", "TaskNotice"],
            Enum.GetNames<MessageTemplateKind>());
}
