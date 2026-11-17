using System.Text.Json;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Soporte fase 4b (H16, H18): la plantilla de la encuesta de satisfacción. Lleva tres botones de respuesta rápida fijos (Buena,
/// Regular, Mala), que el tenant no edita porque el toque se lee por su texto; sin encabezado; nombre reservado por versiones
/// (<c>qep_soporte_encuesta</c>…); y sólo se adopta una externa con exactamente esos tres botones en ese orden.</summary>
public sealed class SupportSurveyTemplateDefinitionTests
{
    [Fact]
    public void TheButtonsAreTheThreeFixedQuickReplies()
    {
        Assert.Equal(["Buena", "Regular", "Mala"], SupportSurveyTemplateDefinition.ButtonLabels);
        Assert.Equal(("qep_soporte_encuesta", "qep_soporte_encuesta_"), (MessageTemplateName.SupportSurveyName, MessageTemplateName.SupportSurveyPrefix));
        Assert.Equal(
            (MessageTemplateName.SupportSurveyName, "es", "UTILITY"),
            (SupportSurveyTemplateDefinition.Name, SupportSurveyTemplateDefinition.Language, SupportSurveyTemplateDefinition.Category));
        Assert.Equal("qep_soporte_encuesta_v2", SupportSurveyTemplateDefinition.NameFor(2));
        Assert.Equal(2, SupportSurveyTemplateDefinition.VersionOf("qep_soporte_encuesta_v2"));
        Assert.Null(SupportSurveyTemplateDefinition.VersionOf("qep_soporte_formal_v2"));
        Assert.True(MessageTemplateName.IsReserved("qep_soporte_encuesta_x"));

        using var payload = JsonDocument.Parse(SupportSurveyTemplateDefinition
            .Payload(SupportSurveyTemplateDefinition.Name, SupportSurveyTemplateDefinition.BaseBody).ToJsonString());
        var components = payload.RootElement.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal(["BODY", "BUTTONS"], components.Select(component => component.GetProperty("type").GetString()));
        Assert.Equal(
            ["QUICK_REPLY|Buena", "QUICK_REPLY|Regular", "QUICK_REPLY|Mala"],
            components[1].GetProperty("buttons").EnumerateArray()
                .Select(button => $"{button.GetProperty("type").GetString()}|{button.GetProperty("text").GetString()}"));
        Assert.True(SupportSurveyTemplateDefinition.IsAdoptable(payload.RootElement.GetProperty("components").GetRawText()));
    }

    [Fact]
    public void TheBaseBodyNeitherStartsNorEndsWithAVariable()
    {
        Assert.Equal(
            "Hola {{nombre_contacto}}, tu caso {{radicado}} quedó resuelto. ¿Cómo calificas la atención que recibiste?",
            SupportSurveyTemplateDefinition.BaseBody);
        var body = SupportSurveyTemplateDefinition.BaseBody.Trim();

        Assert.DoesNotMatch(@"^\{\{[a-z0-9_]+\}\}", body);
        Assert.DoesNotMatch(@"\{\{[a-z0-9_]+\}\}[.!?]?$", body);
        Assert.Equal(["nombre_contacto", "radicado"], TemplateComponentsBuilder.VariablesOf(body));
        Assert.Null(TemplateBodyRules.Check(body, SystemTemplateVariables.KeysOf(SystemTemplateVariables.SupportSurvey)));
        Assert.All(SupportSurveyTemplateDefinition.Examples.Values, example => Assert.DoesNotMatch("[$#%]", example));
    }

    [Theory]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Buena"},{"type":"QUICK_REPLY","text":"Regular"},{"type":"QUICK_REPLY","text":"Mala"}]}]""", true)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Mala"},{"type":"QUICK_REPLY","text":"Regular"},{"type":"QUICK_REPLY","text":"Buena"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Buena"},{"type":"QUICK_REPLY","text":"Regular"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Buena"},{"type":"QUICK_REPLY","text":"Regular"},{"type":"QUICK_REPLY","text":"Mala"},{"type":"QUICK_REPLY","text":"Otra"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."},{"type":"BUTTONS","buttons":[{"type":"URL","text":"Buena","url":"https://x.co"},{"type":"QUICK_REPLY","text":"Regular"},{"type":"QUICK_REPLY","text":"Mala"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, caso {{radicado}}."}]""", false)]
    [InlineData("""[{"type":"HEADER","format":"TEXT","text":"Encuesta"},{"type":"BODY","text":"Hola {{nombre_contacto}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Buena"},{"type":"QUICK_REPLY","text":"Regular"},{"type":"QUICK_REPLY","text":"Mala"}]}]""", false)]
    [InlineData("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, respuesta {{respuesta}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Buena"},{"type":"QUICK_REPLY","text":"Regular"},{"type":"QUICK_REPLY","text":"Mala"}]}]""", false)]
    [InlineData("no es json", false)]
    public void ATemplateWithOtherButtonsIsNotAdoptable(string components, bool expected) =>
        Assert.Equal(expected, SupportSurveyTemplateDefinition.IsAdoptable(components));
}
