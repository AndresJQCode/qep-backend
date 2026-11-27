using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>HR birthday notices: the definition of the HrBirthdayNotice WhatsApp template, a body-only sibling of the task notice. It never
/// carries a birth year or an age, and it uses only four variables (no advisor, company or greeting).</summary>
public sealed class HrBirthdayNoticeTemplateDefinitionTests
{
    [Fact]
    public void BaseBodyUsesOnlyItsFourVariables()
    {
        var keys = SystemTemplateVariables.KeysOf(SystemTemplateVariables.HrBirthdayNotice);

        Assert.True(keys.SetEquals(["nombre_contacto", "hoy", "fecha", "proximos"]));
        Assert.Null(TemplateBodyRules.Check(HrBirthdayNoticeTemplateDefinition.BaseBody, keys));
        Assert.Equal(
            "Hola {{nombre_contacto}}, estos son los cumpleaños del equipo. Hoy: {{hoy}}. Próximos ({{fecha}}): {{proximos}}. Puedes ver el detalle en la campana de QEP.",
            HrBirthdayNoticeTemplateDefinition.BaseBody);
        Assert.Equal(("qep_cumpleanos_equipo", "es", "UTILITY", "hoy", "fecha", "proximos"), (
            HrBirthdayNoticeTemplateDefinition.Name, HrBirthdayNoticeTemplateDefinition.Language, HrBirthdayNoticeTemplateDefinition.Category,
            HrBirthdayNoticeTemplateDefinition.TodayKey, HrBirthdayNoticeTemplateDefinition.DateKey, HrBirthdayNoticeTemplateDefinition.UpcomingKey));
    }

    [Fact]
    public void NoVariableIsAnAgeOrABirthYear() =>
        Assert.DoesNotContain(
            SystemTemplateVariables.KeysOf(SystemTemplateVariables.HrBirthdayNotice),
            key => key.Contains("edad", StringComparison.Ordinal) || key.Contains("nacimiento", StringComparison.Ordinal) || key.Contains("anio", StringComparison.Ordinal));

    [Fact]
    public void NameForAndVersionOfRoundTrip()
    {
        Assert.Equal("qep_cumpleanos_equipo", HrBirthdayNoticeTemplateDefinition.NameFor(1));
        Assert.Equal("qep_cumpleanos_equipo_v2", HrBirthdayNoticeTemplateDefinition.NameFor(2));
        Assert.Equal(1, HrBirthdayNoticeTemplateDefinition.VersionOf("qep_cumpleanos_equipo"));
        Assert.Equal(7, HrBirthdayNoticeTemplateDefinition.VersionOf(HrBirthdayNoticeTemplateDefinition.NameFor(7)));
        Assert.Null(HrBirthdayNoticeTemplateDefinition.VersionOf("qep_cumpleanos_equipo_v02"));
        Assert.Null(HrBirthdayNoticeTemplateDefinition.VersionOf("qep_cumpleanos_equipo_v1"));
        Assert.Null(HrBirthdayNoticeTemplateDefinition.VersionOf("otra"));
        Assert.Null(HrBirthdayNoticeTemplateDefinition.VersionOf(null));
    }

    [Fact]
    public void TheNameAndThePrefixAreReservedAndTheKindFits()
    {
        Assert.True(MessageTemplateName.IsReserved("qep_cumpleanos_equipo"));
        Assert.True(MessageTemplateName.IsReserved("qep_cumpleanos_equipo_v3"));
        Assert.False(MessageTemplateName.IsReserved("qep_cumpleanos"));
        Assert.True(nameof(MessageTemplateKind.HrBirthdayNotice).Length <= MessageTemplate.KindMaxLength);
    }

    [Fact]
    public void IsAdoptableRejectsHeaderOrButtons()
    {
        var own = HrBirthdayNoticeTemplateDefinition.Payload(HrBirthdayNoticeTemplateDefinition.Name, HrBirthdayNoticeTemplateDefinition.BaseBody)["components"]!
            .ToJsonString();

        Assert.True(HrBirthdayNoticeTemplateDefinition.IsAdoptable(own));
        Assert.False(HrBirthdayNoticeTemplateDefinition.IsAdoptable(
            """[{"type":"HEADER","format":"TEXT","text":"Hola"},{"type":"BODY","text":"Hola {{nombre_contacto}}, hoy: {{hoy}}."}]"""));
        Assert.False(HrBirthdayNoticeTemplateDefinition.IsAdoptable(
            """[{"type":"BODY","text":"Hola {{nombre_contacto}}, hoy: {{hoy}}."},{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Ok"}]}]"""));
        Assert.False(HrBirthdayNoticeTemplateDefinition.IsAdoptable("""[{"type":"BODY","text":"Hola {{asesor}}, hoy: {{hoy}}."}]"""));
    }

    [Fact]
    public void AnExternalIsAdoptedAsTheNoticeAndAnotherKindIsNot()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var external = MessageTemplate.Mirrored(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "qep_cumpleanos_equipo", "es", "UTILITY", "NAMED", "APPROVED", null,
            """[{"type":"BODY","text":"Hola"}]""", DateTimeOffset.UnixEpoch);

        external.AdoptAsHrBirthdayNotice(now);

        Assert.Equal((MessageTemplateKind.HrBirthdayNotice, now), (external.Kind, external.UpdatedAt));
        var error = Assert.Throws<MessagingDomainException>(() => external.AdoptAsHrBirthdayNotice(now));
        Assert.Equal(MessagingErrorCodes.TemplateHrBirthdayNoticeConflict, error.Code);
        Assert.Equal(
            ("messaging.template.hr_birthday_notice_exists", "messaging.template.hr_birthday_notice_conflict",
                "messaging.template.hr_birthday_notice_provisioned"),
            (MessagingErrorCodes.TemplateHrBirthdayNoticeExists, MessagingErrorCodes.TemplateHrBirthdayNoticeConflict,
                MessagingAuditActions.TemplateHrBirthdayNoticeProvisioned));
    }
}
