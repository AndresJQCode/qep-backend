using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Cobros recurrentes §5 (plantillas aprobadas por el owner, 2026-10-29): el texto literal de las dos plantillas de cobro y su
/// aprovisionamiento por conexión, con el mismo mecanismo que la plantilla de cotización pero con parámetros posicionales y sin
/// encabezado de documento.</summary>
public sealed class BillingTemplateProvisioningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessagingGraphFailure NameTaken = new(400, 100, "http_400", "Message Template Name Already Exists", "x");

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public Task<IReadOnlyList<ManagedTemplateDto>> ProvisionAsync()
        {
            var membership = new FakeMembershipDirectory();
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            var handler = new ProvisionBillingTemplatesHandler(
                Repository, Bed.Meta, Bed.Connections, new FakeMemberNames(Bed.MemberId), new CallerMembership(membership, executionContext), UnitOfWork, Audit,
                Bed.TenantModules, executionContext, new FakeClock(Bed.Now), new RecordingLogger<ProvisionBillingTemplatesHandler>());
            return handler.HandleAsync(new ProvisionBillingTemplatesCommand(Bed.TenantId, Bed.ConnectionId), Ct);
        }
    }

    [Fact]
    public void TheTwoDefinitionsCarryTheOwnersTextVerbatim()
    {
        var reminder = BillingTemplateDefinitions.Reminder;
        var overdue = BillingTemplateDefinitions.Overdue;

        Assert.Equal("qep_cobro_recordatorio", reminder.Name);
        Assert.Equal("Hola {{1}}, te recordamos que tu servicio {{2}} por {{3}} vence el {{4}}. Si ya realizaste el pago, ignora este mensaje.", reminder.Body);
        Assert.Equal(4, reminder.Examples.Count);
        Assert.Equal(MessageTemplateKind.BillingReminder, reminder.Kind);

        Assert.Equal("qep_cobro_vencido", overdue.Name);
        Assert.Equal("Hola {{1}}, tienes {{2}} cobro(s) vencido(s) por un total de {{3}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.", overdue.Body);
        Assert.Equal(3, overdue.Examples.Count);
        Assert.Equal(MessageTemplateKind.BillingOverdue, overdue.Kind);

        Assert.All(new[] { reminder, overdue }, definition => Assert.Equal(("es", "UTILITY"), (BillingTemplateDefinitions.Language, BillingTemplateDefinitions.Category)));
    }

    [Fact]
    public void ThePayloadIsPositionalWithOneExamplePerVariableAndNoHeaderOrFooter()
    {
        var payload = BillingTemplateDefinitions.Reminder.Payload(BillingTemplateDefinitions.Reminder.Name);

        Assert.Equal("qep_cobro_recordatorio", payload["name"]!.GetValue<string>());
        Assert.Equal("UTILITY", payload["category"]!.GetValue<string>());
        Assert.Null(payload["parameter_format"]);
        var components = payload["components"]!.AsArray();
        var body = Assert.Single(components);
        Assert.Equal("BODY", body!["type"]!.GetValue<string>());
        Assert.Equal(4, body["example"]!["body_text"]![0]!.AsArray().Count);
    }

    [Fact]
    public async Task ProvisioningCreatesBothTemplatesPendingWithTheirKindAndAuditsEach()
    {
        var rig = new Rig();

        var created = await rig.ProvisionAsync();

        Assert.Equal(["qep_cobro_recordatorio", "qep_cobro_vencido"], created.Select(template => template.Name));
        Assert.Equal(
            ["qep_cobro_recordatorio", "qep_cobro_vencido"], rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Equal(
            [MessageTemplateKind.BillingReminder, MessageTemplateKind.BillingOverdue], rig.Repository.Rows.Select(row => row.Kind));
        Assert.All(rig.Repository.Rows, row => Assert.Equal(MessageTemplateStatuses.Pending, row.Status));
        Assert.Equal(2, rig.Audit.Templates.Count);
        Assert.All(rig.Audit.Templates, call => Assert.Equal(MessagingAuditActions.TemplateBillingProvisioned, call.Action));
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ProvisioningTwiceCreatesNothingTheSecondTime()
    {
        var rig = new Rig();
        await rig.ProvisionAsync();
        rig.Bed.Meta.CreatedTemplates.Clear();

        var again = await rig.ProvisionAsync();

        Assert.Equal(2, again.Count);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Equal(2, rig.Repository.Rows.Count);
    }

    [Fact]
    public async Task ARejectedTemplateIsRecreatedAsTheNextVersionAndTheOldOneIsDeleted()
    {
        var rig = new Rig();
        var rejected = MessageTemplate.Pending(
            rig.Bed.TenantId, rig.Bed.ConnectionId, "771", "qep_cobro_recordatorio", "es", "UTILITY", "POSITIONAL", "PENDING", "[]",
            MessageTemplateKind.BillingReminder, rig.Bed.MemberId, rig.Bed.Now.AddDays(-1));
        rejected.ApplyStatus(MessageTemplateStatuses.Rejected, "INVALID_FORMAT", rig.Bed.Now.AddDays(-1));
        rig.Repository.Rows.Add(rejected);

        await rig.ProvisionAsync();

        Assert.Contains("qep_cobro_recordatorio_v2", rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Equal([("771", "qep_cobro_recordatorio")], rig.Bed.Meta.Deleted);
        Assert.DoesNotContain(rig.Repository.Rows, row => row.MetaTemplateId == "771");
    }

    [Fact]
    public async Task ANameAlreadyTakenInMetaJumpsToTheNextVersion()
    {
        var rig = new Rig();
        rig.Bed.Meta.CreateResults.Enqueue(new(null, NameTaken));

        var created = await rig.ProvisionAsync();

        Assert.Equal(["qep_cobro_recordatorio_v2", "qep_cobro_vencido"], created.Select(template => template.Name));
    }

    [Fact]
    public void TheBillingNamesAreReservedForFreeTemplates()
    {
        Assert.True(MessageTemplateName.IsReserved("qep_cobro_recordatorio"));
        Assert.True(MessageTemplateName.IsReserved("qep_cobro_vencido_v3"));
        Assert.False(MessageTemplateName.IsReserved("qep_cobros"));
    }
}
