using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Support task notices: the definition of the TaskNotice WhatsApp template, a body-only sibling of the wholesale invitation notice.
/// It serves both internal tasks and customer tickets, so its text never says "tarea".</summary>
public sealed class TaskNoticeTemplateDefinitionTests
{
    private static readonly string[] ForbiddenFragments = ["asunto", "descripcion", "subject", "description"];

    [Fact]
    public void BaseBodyUsesOnlyTaskNoticeVariables() =>
        Assert.Null(TemplateBodyRules.Check(TaskNoticeTemplateDefinition.BaseBody, SystemTemplateVariables.KeysOf(SystemTemplateVariables.TaskNotice)));

    // The notice never carries the subject or the description: WhatsApp is not where ticket content should travel.
    [Fact]
    public void TheVariablesNeverIncludeSubjectOrDescription()
    {
        var keys = SystemTemplateVariables.KeysOf(SystemTemplateVariables.TaskNotice);

        Assert.True(keys.SetEquals(["nombre_contacto", "asesor", "empresa", "saludo", "numero"]));
        Assert.DoesNotContain(keys, key => ForbiddenFragments.Any(forbidden => key.Contains(forbidden, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheBaseBodyIsTheGenericText() =>
        Assert.Equal("¡Hola! {{asesor}} te asignó {{numero}} en QEP. Ábrelo para ver el detalle.", TaskNoticeTemplateDefinition.BaseBody);

    [Fact]
    public void TheBaseBodyNeverSaysTask() =>
        Assert.DoesNotContain("tarea", TaskNoticeTemplateDefinition.BaseBody, StringComparison.OrdinalIgnoreCase);

    // Meta rejects a body that starts or ends with a variable.
    [Fact]
    public void BaseBodyDoesNotStartOrEndWithAVariable()
    {
        var body = TaskNoticeTemplateDefinition.BaseBody.Trim();

        Assert.DoesNotMatch(@"^\{\{[a-z0-9_]+\}\}", body);
        Assert.DoesNotMatch(@"\{\{[a-z0-9_]+\}\}[.!?]?$", body);
    }

    [Fact]
    public void NameForOneIsTheBareNameAndTwoIsV2()
    {
        Assert.Equal("qep_tarea_asignada", TaskNoticeTemplateDefinition.NameFor(1));
        Assert.Equal("qep_tarea_asignada_v2", TaskNoticeTemplateDefinition.NameFor(2));
        Assert.Equal(1, TaskNoticeTemplateDefinition.VersionOf("qep_tarea_asignada"));
        Assert.Equal(2, TaskNoticeTemplateDefinition.VersionOf("qep_tarea_asignada_v2"));
    }

    [Theory]
    [InlineData("qep_tarea_asignada_v02")]
    [InlineData("qep_tarea_asignada_v1")]
    [InlineData("otra")]
    [InlineData(null)]
    public void VersionOfRejectsForeignNames(string? name) => Assert.Null(TaskNoticeTemplateDefinition.VersionOf(name));

    [Fact]
    public void TheReservedPrefixCoversTheName()
    {
        Assert.True(MessageTemplateName.IsReserved("qep_tarea_asignada"));
        Assert.True(MessageTemplateName.IsReserved("qep_tarea_asignada_v3"));
        Assert.False(MessageTemplateName.IsReserved("qep_tarea"));
    }

    [Fact]
    public void TheKindFitsItsColumn() =>
        Assert.True(nameof(MessageTemplateKind.TaskNotice).Length <= MessageTemplate.KindMaxLength);

    [Fact]
    public void TheDefinitionIsBodyOnlyInSpanishUtility()
    {
        Assert.Equal(("qep_tarea_asignada", "es", "UTILITY", "numero"), (
            TaskNoticeTemplateDefinition.Name, TaskNoticeTemplateDefinition.Language, TaskNoticeTemplateDefinition.Category,
            TaskNoticeTemplateDefinition.NumberKey));
        var draft = TaskNoticeTemplateDefinition.Draft(TaskNoticeTemplateDefinition.Name, TaskNoticeTemplateDefinition.BaseBody);
        Assert.Null(draft.Footer);
        Assert.Empty(draft.Buttons);
        var own = TaskNoticeTemplateDefinition.Payload(TaskNoticeTemplateDefinition.Name, TaskNoticeTemplateDefinition.BaseBody)["components"]!.ToJsonString();
        Assert.True(TaskNoticeTemplateDefinition.IsAdoptable(own));
        Assert.False(TaskNoticeTemplateDefinition.IsAdoptable("""[{"type":"BODY","text":"Hola {{nombre_contacto}}, {{numero_pedido}}."}]"""));
    }

    [Fact]
    public void AnExternalIsAdoptedAsTheNoticeAndAnotherKindIsNot()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var external = MessageTemplate.Mirrored(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "1", "qep_tarea_asignada", "es", "UTILITY", "NAMED", "APPROVED", null,
            """[{"type":"BODY","text":"Hola"}]""", DateTimeOffset.UnixEpoch);

        external.AdoptAsTaskNotice(now);

        Assert.Equal((MessageTemplateKind.TaskNotice, now), (external.Kind, external.UpdatedAt));
        var error = Assert.Throws<MessagingDomainException>(() => external.AdoptAsTaskNotice(now));
        Assert.Equal(MessagingErrorCodes.TemplateTaskNoticeConflict, error.Code);
    }

    [Fact]
    public void TheErrorCodesAreTheContractOnes() =>
        Assert.Equal(
            ("messaging.template.task_notice_exists", "messaging.template.task_notice_conflict"),
            (MessagingErrorCodes.TemplateTaskNoticeExists, MessagingErrorCodes.TemplateTaskNoticeConflict));
}
