using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Agenda: la definición de la plantilla del recordatorio de cita, calcada de la del aviso de invitación de Mayorista. Sólo cuerpo,
/// en <c>es</c> y <c>UTILITY</c>, con las cuatro variables fijas más <c>nombre_cliente</c>, <c>servicio</c>, <c>fecha_hora</c>, <c>recurso</c> y
/// <c>sede</c>; nombre reservado con sus versiones (<c>qep_agenda_recordatorio</c>, <c>qep_agenda_recordatorio_v2</c>…) e
/// <c>IsAdoptable</c> que mira la forma.</summary>
public sealed class AppointmentReminderTemplateDefinitionTests
{
    private const string ApprovedText =
        "¡Hola, {{nombre_cliente}}! Te recordamos tu cita de {{servicio}} el {{fecha_hora}} con {{recurso}} en {{sede}}. "
        + "Si no puedes asistir, avísanos con tiempo para reprogramarla.";

    [Fact]
    public void TheDefinitionIsTheReservedNameInSpanishUtility()
    {
        Assert.Equal(
            ("qep_agenda_recordatorio", "qep_agenda_recordatorio_"),
            (MessageTemplateName.AppointmentReminderName, MessageTemplateName.AppointmentReminderPrefix));
        Assert.Equal(
            ("qep_agenda_recordatorio", "es", "UTILITY"),
            (AppointmentReminderTemplateDefinition.Name, AppointmentReminderTemplateDefinition.Language,
                AppointmentReminderTemplateDefinition.Category));
        Assert.Equal(
            ("nombre_cliente", "servicio", "fecha_hora", "recurso", "sede"),
            (AppointmentReminderTemplateDefinition.ClientKey, AppointmentReminderTemplateDefinition.ServiceKey,
                AppointmentReminderTemplateDefinition.DateTimeKey, AppointmentReminderTemplateDefinition.ResourceKey,
                AppointmentReminderTemplateDefinition.LocationKey));
        Assert.Equal(
            SystemTemplateVariables.AppointmentReminder.ToDictionary(variable => variable.Key, variable => variable.Example),
            AppointmentReminderTemplateDefinition.Examples);
    }

    [Fact]
    public void TheBaseBodyIsTheApprovedText()
    {
        Assert.Equal(ApprovedText, AppointmentReminderTemplateDefinition.BaseBody);
        var draft = AppointmentReminderTemplateDefinition.Draft(
            AppointmentReminderTemplateDefinition.Name, AppointmentReminderTemplateDefinition.BaseBody);
        Assert.Null(draft.Footer);
        Assert.Empty(draft.Buttons);
    }

    [Fact]
    public void TheVariablesAreTheFixedOnesPlusTheAppointmentOnes()
    {
        Assert.Equal(
            ["nombre_contacto", "asesor", "empresa", "saludo", "nombre_cliente", "servicio", "fecha_hora", "recurso", "sede"],
            SystemTemplateVariables.AppointmentReminder.Select(variable => variable.Key));
        var dateTime = SystemTemplateVariables.AppointmentReminder.Single(variable => variable.Key == "fecha_hora");
        Assert.Equal("Fecha y hora de la cita", dateTime.Label);
        Assert.Equal(
            ["nombre_cliente", "servicio", "fecha_hora", "recurso", "sede"],
            TemplateComponentsBuilder.VariablesOf(AppointmentReminderTemplateDefinition.BaseBody));
    }

    // Meta rechaza un cuerpo que empieza o termina con una variable.
    [Fact]
    public void TheBaseBodyNeitherStartsNorEndsWithAVariable()
    {
        var body = AppointmentReminderTemplateDefinition.BaseBody.Trim();

        Assert.DoesNotMatch(@"^\{\{[a-z0-9_]+\}\}", body);
        Assert.DoesNotMatch(@"\{\{[a-z0-9_]+\}\}[.!?]?$", body);
    }

    // Meta rechaza la plantilla si un ejemplo lleva $, # o % (README § Plantilla de WhatsApp).
    [Fact]
    public void NoExampleCarriesACharacterMetaRejects() =>
        Assert.All(AppointmentReminderTemplateDefinition.Examples.Values, example => Assert.DoesNotMatch("[$#%]", example));

    [Fact]
    public void ThePayloadHasOnlyTheBodyWithNamedExamples()
    {
        using var payload = JsonDocument.Parse(AppointmentReminderTemplateDefinition.Payload(
            AppointmentReminderTemplateDefinition.Name, AppointmentReminderTemplateDefinition.BaseBody).ToJsonString());
        var root = payload.RootElement;

        Assert.Equal(("qep_agenda_recordatorio", "es", "UTILITY", "NAMED"), (
            root.GetProperty("name").GetString(), root.GetProperty("language").GetString(), root.GetProperty("category").GetString(),
            root.GetProperty("parameter_format").GetString()));
        var component = Assert.Single(root.GetProperty("components").EnumerateArray());
        Assert.Equal("BODY", component.GetProperty("type").GetString());
        Assert.Equal(AppointmentReminderTemplateDefinition.BaseBody, component.GetProperty("text").GetString());
        Assert.Equal(
            [
                "nombre_cliente|María González", "servicio|Consulta general", "fecha_hora|martes 24 de noviembre a las 15:00",
                "recurso|Dra. Paula Ruiz", "sede|Sede Centro",
            ],
            component.GetProperty("example").GetProperty("body_text_named_params").EnumerateArray()
                .Select(example => $"{example.GetProperty("param_name").GetString()}|{example.GetProperty("example").GetString()}"));
    }

    [Theory]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_cliente}}, tu cita de {{servicio}} es el {{fecha_hora}}."}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_cliente}}, tienes una cita pronto."}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_cliente}}, pedido {{numero_pedido}}."}]""", false)]
    // Sin clave vieja admitida: el envío no sabe llenar fullname.
    [InlineData("""[{"type":"BODY","text":"Hola {{fullname}}, tu cita es el {{fecha_hora}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Cita"},{"type":"BODY","text":"Hola {{nombre_cliente}}, tienes una cita."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"Hola {{nombre_cliente}}, tienes una cita."}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_cliente}}, tienes una cita."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Sí"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"   "}]""", false)]
    [InlineData("no es json", false)]
    public void IsAdoptableAcceptsItsOwnComponentsAndRejectsAHeaderOrButtons(string components, bool expected)
    {
        var own = AppointmentReminderTemplateDefinition.Payload(
            AppointmentReminderTemplateDefinition.Name, AppointmentReminderTemplateDefinition.BaseBody)["components"]!.ToJsonString();

        Assert.True(AppointmentReminderTemplateDefinition.IsAdoptable(own));
        Assert.Equal(expected, AppointmentReminderTemplateDefinition.IsAdoptable(components));
    }

    [Theory]
    [InlineData(1, "qep_agenda_recordatorio")]
    [InlineData(2, "qep_agenda_recordatorio_v2")]
    [InlineData(10, "qep_agenda_recordatorio_v10")]
    public void NameForAndVersionOfRoundTrip(int version, string expected)
    {
        Assert.Equal(expected, AppointmentReminderTemplateDefinition.NameFor(version));
        Assert.Equal(version, AppointmentReminderTemplateDefinition.VersionOf(expected));
        Assert.True(MessageTemplateName.IsReserved(expected));
    }

    [Theory]
    [InlineData("qep_agenda_recordatorio_v1")]
    [InlineData("qep_agenda_recordatorio_v0")]
    [InlineData("qep_agenda_recordatorio_v02")]
    [InlineData("qep_agenda_recordatorio_v")]
    [InlineData("qep_agenda_recordatorios")]
    [InlineData("qep_pedido_v2")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionOfIsNullForAnythingThatIsNotAVersionOfTheReminder(string? name) =>
        Assert.Null(AppointmentReminderTemplateDefinition.VersionOf(name));

    [Fact]
    public void NameForRejectsAVersionBelowOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AppointmentReminderTemplateDefinition.NameFor(0));

    // El prefijo reservado cubre cualquier cosa debajo, no sólo las versiones: una libre no puede tomar «qep_agenda_recordatorio_x».
    [Theory]
    [InlineData("qep_agenda_recordatorio", true)]
    [InlineData("qep_agenda_recordatorio_x", true)]
    [InlineData("qep_agenda_recordatorios", false)]
    [InlineData("qep_agenda", false)]
    public void TheNameAndThePrefixAreReserved(string name, bool reserved) =>
        Assert.Equal(reserved, MessageTemplateName.IsReserved(name));

    [Fact]
    public void AnExternalIsAdoptedAsTheReminderAndAnotherKindIsNot()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var external = MessageTemplate.Mirrored(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "qep_agenda_recordatorio", "es", "UTILITY", "NAMED", "APPROVED", null,
            """[{"type":"BODY","text":"Hola"}]""", DateTimeOffset.UnixEpoch);

        external.AdoptAsAppointmentReminder(now);

        Assert.Equal((MessageTemplateKind.AppointmentReminder, now), (external.Kind, external.UpdatedAt));
        var error = Assert.Throws<MessagingDomainException>(() => external.AdoptAsAppointmentReminder(now));
        Assert.Equal(MessagingErrorCodes.TemplateAppointmentReminderConflict, error.Code);
    }

    [Fact]
    public void TheErrorCodesAreTheContractOnes() =>
        Assert.Equal(
            ("messaging.template.appointment_reminder_exists", "messaging.template.appointment_reminder_conflict"),
            (MessagingErrorCodes.TemplateAppointmentReminderExists, MessagingErrorCodes.TemplateAppointmentReminderConflict));

    [Fact]
    public void TheKindIsStoredByNameAtTheEnd() =>
        Assert.Equal(MessageTemplateKind.AppointmentReminder, Enum.GetValues<MessageTemplateKind>()[^2]);

    // La columna `kind` es varchar(32): el nombre del tipo nuevo tiene que caber.
    [Fact]
    public void TheKindNameFitsTheColumn() =>
        Assert.True(nameof(MessageTemplateKind.AppointmentReminder).Length <= MessageTemplate.KindMaxLength);
}
