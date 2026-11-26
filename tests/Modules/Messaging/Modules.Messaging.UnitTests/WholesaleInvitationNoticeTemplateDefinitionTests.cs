using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Mayorista: la definición de la plantilla del aviso de invitación de distribuidor, calcada de la «Aviso de respuesta» de Soporte.
/// Sólo cuerpo, en <c>es</c> y <c>UTILITY</c>, con las cuatro variables fijas más <c>mayorista</c> y <c>correo</c>; nombre reservado con sus
/// versiones (<c>qep_mayorista_invitacion</c>, <c>qep_mayorista_invitacion_v2</c>…) e <c>IsAdoptable</c> que mira la forma.</summary>
public sealed class WholesaleInvitationNoticeTemplateDefinitionTests
{
    private const string ApprovedText =
        "¡Hola, {{nombre_contacto}}! {{mayorista}} te invitó como distribuidor en la plataforma Lulo CRM. Te enviamos la invitación al "
        + "correo {{correo}}. Si no la encuentras en tu bandeja de entrada, revisa la carpeta de spam o correo no deseado.";

    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal(
            ("qep_mayorista_invitacion", "qep_mayorista_invitacion_"),
            (MessageTemplateName.WholesaleInvitationNoticeName, MessageTemplateName.WholesaleInvitationNoticePrefix));
        Assert.Equal(
            ("qep_mayorista_invitacion", "es", "UTILITY"),
            (WholesaleInvitationNoticeTemplateDefinition.Name, WholesaleInvitationNoticeTemplateDefinition.Language,
                WholesaleInvitationNoticeTemplateDefinition.Category));
        Assert.Equal(("mayorista", "correo"), (WholesaleInvitationNoticeTemplateDefinition.WholesalerKey, WholesaleInvitationNoticeTemplateDefinition.EmailKey));
        Assert.Equal(
            SystemTemplateVariables.WholesaleInvitationNotice.ToDictionary(variable => variable.Key, variable => variable.Example),
            WholesaleInvitationNoticeTemplateDefinition.Examples);
    }

    [Fact]
    public void TheBaseBodyIsTheApprovedText()
    {
        Assert.Equal(ApprovedText, WholesaleInvitationNoticeTemplateDefinition.BaseBody);
        var draft = WholesaleInvitationNoticeTemplateDefinition.Draft(
            WholesaleInvitationNoticeTemplateDefinition.Name, WholesaleInvitationNoticeTemplateDefinition.BaseBody);
        Assert.Null(draft.Footer);
        Assert.Empty(draft.Buttons);
    }

    [Fact]
    public void TheVariablesAreTheFixedOnesPlusWholesalerAndEmail()
    {
        Assert.Equal(
            ["nombre_contacto", "asesor", "empresa", "saludo", "mayorista", "correo"],
            SystemTemplateVariables.WholesaleInvitationNotice.Select(variable => variable.Key));
        var wholesaler = SystemTemplateVariables.WholesaleInvitationNotice.Single(variable => variable.Key == "mayorista");
        var email = SystemTemplateVariables.WholesaleInvitationNotice.Single(variable => variable.Key == "correo");
        Assert.Equal(("Nombre del mayorista", "Distribuciones Andinas"), (wholesaler.Label, wholesaler.Example));
        Assert.Equal(("Correo de la invitación", "compras@distribuidora.co"), (email.Label, email.Example));
        Assert.Equal(
            ["nombre_contacto", "mayorista", "correo"], TemplateComponentsBuilder.VariablesOf(WholesaleInvitationNoticeTemplateDefinition.BaseBody));
    }

    // Meta rechaza un cuerpo que empieza o termina con una variable.
    [Fact]
    public void TheBaseBodyNeitherStartsNorEndsWithAVariable()
    {
        var body = WholesaleInvitationNoticeTemplateDefinition.BaseBody.Trim();

        Assert.DoesNotMatch(@"^\{\{[a-z0-9_]+\}\}", body);
        Assert.DoesNotMatch(@"\{\{[a-z0-9_]+\}\}[.!?]?$", body);
    }

    // El token y el link de la invitación nunca llegan a Mensajería: el aviso sólo dice a qué correo se mandó.
    [Fact]
    public void TheBaseBodyNamesNoLinkNorToken()
    {
        Assert.DoesNotContain("http", WholesaleInvitationNoticeTemplateDefinition.BaseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", WholesaleInvitationNoticeTemplateDefinition.BaseBody, StringComparison.OrdinalIgnoreCase);
    }

    // Meta rechaza la plantilla si un ejemplo lleva $, # o % (README § Plantilla de WhatsApp).
    [Fact]
    public void NoExampleCarriesACharacterMetaRejects() =>
        Assert.All(WholesaleInvitationNoticeTemplateDefinition.Examples.Values, example => Assert.DoesNotMatch("[$#%]", example));

    [Fact]
    public void ThePayloadHasOnlyTheBodyWithNamedExamples()
    {
        using var payload = JsonDocument.Parse(WholesaleInvitationNoticeTemplateDefinition.Payload(
            WholesaleInvitationNoticeTemplateDefinition.Name, WholesaleInvitationNoticeTemplateDefinition.BaseBody).ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_mayorista_invitacion", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(),
            root.GetProperty("parameter_format").GetString()));
        var component = Assert.Single(root.GetProperty("components").EnumerateArray());
        Assert.Equal("BODY", component.GetProperty("type").GetString());
        Assert.Equal(WholesaleInvitationNoticeTemplateDefinition.BaseBody, component.GetProperty("text").GetString());
        Assert.Equal(
            ["nombre_contacto|María González", "mayorista|Distribuciones Andinas", "correo|compras@distribuidora.co"],
            component.GetProperty("example").GetProperty("body_text_named_params").EnumerateArray()
                .Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Theory]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, {{mayorista}} te invitó; revisa {{correo}}."}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, te invitaron a la plataforma."}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, pedido {{numero_pedido}}."}]""", false)]
    // Sin clave vieja admitida: el envío no sabe llenar fullname.
    [InlineData("""[{"type":"BODY","text":"Hola {{fullname}}, {{mayorista}} te invitó."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Invitación"},{"type":"BODY","text":"Hola {{nombre_contacto}}, te invitaron."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_contacto}}, te invitaron."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, te invitaron."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"   "}]""", false)]
    [InlineData("no es json", false)]
    public void IsAdoptableAcceptsItsOwnComponentsAndRejectsAHeaderOrButtons(string components, bool expected)
    {
        var own = WholesaleInvitationNoticeTemplateDefinition.Payload(
            WholesaleInvitationNoticeTemplateDefinition.Name, WholesaleInvitationNoticeTemplateDefinition.BaseBody)["components"]!.ToJsonString();

        Assert.True(WholesaleInvitationNoticeTemplateDefinition.IsAdoptable(own));
        Assert.Equal(expected, WholesaleInvitationNoticeTemplateDefinition.IsAdoptable(components));
    }

    [Theory]
    [InlineData(1, "qep_mayorista_invitacion")]
    [InlineData(2, "qep_mayorista_invitacion_v2")]
    [InlineData(10, "qep_mayorista_invitacion_v10")]
    public void NameForAndVersionOfRoundTrip(int version, string expected)
    {
        Assert.Equal(expected, WholesaleInvitationNoticeTemplateDefinition.NameFor(version));
        Assert.Equal(version, WholesaleInvitationNoticeTemplateDefinition.VersionOf(expected));
        Assert.True(MessageTemplateName.IsReserved(expected));
    }

    [Theory]
    [InlineData("qep_mayorista_invitacion_v1")]
    [InlineData("qep_mayorista_invitacion_v0")]
    [InlineData("qep_mayorista_invitacion_v02")]
    [InlineData("qep_mayorista_invitacion_v")]
    [InlineData("qep_mayorista_invitaciones")]
    [InlineData("qep_pedido_v2")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionOfIsNullForAnythingThatIsNotAVersionOfTheNotice(string? name) =>
        Assert.Null(WholesaleInvitationNoticeTemplateDefinition.VersionOf(name));

    [Fact]
    public void NameForRejectsAVersionBelowOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WholesaleInvitationNoticeTemplateDefinition.NameFor(0));

    // El prefijo reservado cubre cualquier cosa debajo, no sólo las versiones: una libre no puede tomar «qep_mayorista_invitacion_x».
    [Theory]
    [InlineData("qep_mayorista_invitacion", true)]
    [InlineData("qep_mayorista_invitacion_x", true)]
    [InlineData("qep_mayorista_invitaciones", false)]
    [InlineData("qep_mayorista", false)]
    public void TheNameAndThePrefixAreReserved(string name, bool reserved) =>
        Assert.Equal(reserved, MessageTemplateName.IsReserved(name));

    [Fact]
    public void AnExternalIsAdoptedAsTheNoticeAndAnotherKindIsNot()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var external = MessageTemplate.Mirrored(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "qep_mayorista_invitacion", "es", "UTILITY", "NAMED", "APPROVED", null,
            """[{"type":"BODY","text":"Hola"}]""", DateTimeOffset.UnixEpoch);

        external.AdoptAsWholesaleInvitationNotice(now);

        Assert.Equal((MessageTemplateKind.WholesaleInvitationNotice, now), (external.Kind, external.UpdatedAt));
        var error = Assert.Throws<MessagingDomainException>(() => external.AdoptAsWholesaleInvitationNotice(now));
        Assert.Equal(MessagingErrorCodes.TemplateWholesaleInvitationNoticeConflict, error.Code);
    }

    [Fact]
    public void TheErrorCodesAreTheContractOnes() =>
        Assert.Equal(
            ("messaging.template.wholesale_invitation_notice_exists", "messaging.template.wholesale_invitation_notice_conflict"),
            (MessagingErrorCodes.TemplateWholesaleInvitationNoticeExists, MessagingErrorCodes.TemplateWholesaleInvitationNoticeConflict));

    [Fact]
    public void TheKindIsStoredByNameAtTheEnd() =>
        Assert.Equal(MessageTemplateKind.WholesaleInvitationNotice, Enum.GetValues<MessageTemplateKind>()[^4]);
}
