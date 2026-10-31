using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Cobros recurrentes §5 y plantillas editables: el texto base de las dos plantillas de cobro, su payload con variables con nombre
/// y su aprovisionamiento por conexión, una plantilla a la vez, con el cuerpo que el admin edite o el base.</summary>
public sealed class BillingTemplateProvisioningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessagingGraphFailure NameTaken = new(400, 100, "http_400", "Message Template Name Already Exists", "x");

    private const string ReminderBody = "Hola {{nombre_contacto}}, tu servicio {{servicio}} vence el {{vencimiento}}. Gracias por estar con nosotros.";
    private const string OverdueBody = "Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobros vencidos. Escríbenos y los revisamos juntos.";

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public Task<IReadOnlyList<ManagedTemplateDto>> ProvisionAsync(string? reminderBody = null, string? overdueBody = null)
        {
            var membership = new FakeMembershipDirectory();
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            var handler = new ProvisionBillingTemplatesHandler(
                Repository, Bed.Meta, Bed.Connections, new FakeMemberNames(Bed.MemberId), new CallerMembership(membership, executionContext), UnitOfWork, Audit,
                Bed.TenantModules, executionContext, new FakeClock(Bed.Now), new RecordingLogger<ProvisionBillingTemplatesHandler>(),
                new ProvisionBillingTemplatesCommandValidator());
            return handler.HandleAsync(new ProvisionBillingTemplatesCommand(Bed.TenantId, Bed.ConnectionId, reminderBody, overdueBody), Ct);
        }

        public static string BodyOf(System.Text.Json.Nodes.JsonObject payload) =>
            payload["components"]!.AsArray().Single(component => component!["type"]!.GetValue<string>() == "BODY")!["text"]!.GetValue<string>();
    }

    [Fact]
    public void TheBaseTextsAreTheOwnersWordsWithNamedVariables()
    {
        Assert.Equal("Hola {{nombre_contacto}}, te recordamos que tu servicio {{servicio}} por {{valor}} vence el {{vencimiento}}. Si ya realizaste el pago, ignora este mensaje.", BillingTemplateDefinitions.Reminder.BaseBody);
        Assert.Equal("Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobro(s) vencido(s) por un total de {{total_vencido}}. Escríbenos para ponerte al día. Si ya pagaste, ignora este mensaje.", BillingTemplateDefinitions.Overdue.BaseBody);
    }

    [Fact]
    public void EachDefinitionCarriesItsKindNameLabelAndVariables()
    {
        var reminder = BillingTemplateDefinitions.Reminder;
        var overdue = BillingTemplateDefinitions.Overdue;

        Assert.Equal(("qep_cobro_recordatorio", "Recordatorio", MessageTemplateKind.BillingReminder), (reminder.Name, reminder.Label, reminder.Kind));
        Assert.Equal(("qep_cobro_vencido", "Vencido", MessageTemplateKind.BillingOverdue), (overdue.Name, overdue.Label, overdue.Kind));
        Assert.Same(SystemTemplateVariables.BillingReminder, reminder.Variables);
        Assert.Same(SystemTemplateVariables.BillingOverdue, overdue.Variables);
        Assert.Equal(["nombre_contacto", "servicio", "valor", "vencimiento"], reminder.LegacyPositionalKeys);
        Assert.Equal(["nombre_contacto", "cantidad_cobros", "total_vencido"], overdue.LegacyPositionalKeys);
        Assert.All(new[] { reminder, overdue }, _ => Assert.Equal(("es", "UTILITY"), (BillingTemplateDefinitions.Language, BillingTemplateDefinitions.Category)));
    }

    [Fact]
    public void ThePayloadIsNamedWithoutHeaderOrFooter()
    {
        var payload = BillingTemplateDefinitions.Reminder.Payload("qep_cobro_recordatorio", "Hola {{nombre_contacto}}, gracias.");

        Assert.Equal("qep_cobro_recordatorio", payload["name"]!.GetValue<string>());
        Assert.Equal("UTILITY", payload["category"]!.GetValue<string>());
        Assert.Equal("NAMED", payload["parameter_format"]!.GetValue<string>());
        var body = Assert.Single(payload["components"]!.AsArray());
        Assert.Equal("BODY", body!["type"]!.GetValue<string>());
        var named = body["example"]!["body_text_named_params"]!.AsArray();
        Assert.Single(named);
        Assert.Equal("nombre_contacto", named[0]!["param_name"]!.GetValue<string>());
        Assert.Equal("María González", named[0]!["example"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProvisioningCreatesBothTemplatesPendingWithTheirKindAndAuditsEachSavingOneAtATime()
    {
        var rig = new Rig();

        var created = await rig.ProvisionAsync();

        Assert.Equal(["qep_cobro_recordatorio", "qep_cobro_vencido"], created.Select(template => template.Name));
        Assert.Equal(
            ["qep_cobro_recordatorio", "qep_cobro_vencido"], rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Equal(
            [BillingTemplateDefinitions.Reminder.BaseBody, BillingTemplateDefinitions.Overdue.BaseBody], rig.Bed.Meta.CreatedTemplates.Select(Rig.BodyOf));
        Assert.Equal(
            [MessageTemplateKind.BillingReminder, MessageTemplateKind.BillingOverdue], rig.Repository.Rows.Select(row => row.Kind));
        Assert.All(rig.Repository.Rows, row => Assert.Equal(("NAMED", MessageTemplateStatuses.Pending), (row.ParameterFormat, row.Status)));
        Assert.Equal(2, rig.Audit.Templates.Count);
        Assert.All(rig.Audit.Templates, call => Assert.Equal(MessagingAuditActions.TemplateBillingProvisioned, call.Action));
        Assert.Equal(2, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task EachBodyGoesToItsTemplate()
    {
        var rig = new Rig();

        var created = await rig.ProvisionAsync("  " + ReminderBody + "  ", OverdueBody);

        Assert.Equal([ReminderBody, OverdueBody], rig.Bed.Meta.CreatedTemplates.Select(Rig.BodyOf));
        Assert.Equal([ReminderBody, OverdueBody], created.Select(template => template.Body));
    }

    [Fact]
    public async Task AMissingBodyFallsBackToThatTemplatesBaseText()
    {
        var rig = new Rig();

        await rig.ProvisionAsync(reminderBody: ReminderBody);

        Assert.Equal([ReminderBody, BillingTemplateDefinitions.Overdue.BaseBody], rig.Bed.Meta.CreatedTemplates.Select(Rig.BodyOf));
    }

    [Fact]
    public async Task AVariableOfTheOtherNoticeIsRejectedWithItsKey()
    {
        var reminder = new Rig();
        var wrongReminder = await Assert.ThrowsAsync<ValidationException>(() => reminder.ProvisionAsync(reminderBody: "Hola {{nombre_contacto}}, tienes {{cantidad_cobros}} cobros."));
        Assert.Equal("reminderBody", Assert.Single(wrongReminder.Errors).PropertyName);
        Assert.Empty(reminder.Bed.Meta.CreatedTemplates);
        Assert.Empty(reminder.Repository.Rows);

        var overdue = new Rig();
        var wrongOverdue = await Assert.ThrowsAsync<ValidationException>(() => overdue.ProvisionAsync(overdueBody: "Hola {{nombre_contacto}}, tu servicio {{servicio}} venció."));
        Assert.Equal("overdueBody", Assert.Single(wrongOverdue.Errors).PropertyName);
        Assert.Empty(overdue.Bed.Meta.CreatedTemplates);
        Assert.Equal(0, overdue.UnitOfWork.Saves);
    }

    // Review Focus 5.
    [Fact]
    public async Task WhenMetaRejectsTheSecondTheFirstStaysSavedAndARetryCreatesOnlyTheSecond()
    {
        var rig = new Rig();
        rig.Bed.Meta.CreateResults.Enqueue(new(new CreatedTemplate("meta-1", "PENDING", "UTILITY"), null));
        rig.Bed.Meta.CreateResults.Enqueue(new(null, new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "El cuerpo no sirve")));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, error.Code);
        Assert.StartsWith("Vencido: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("El cuerpo no sirve", error.Message, StringComparison.Ordinal);
        Assert.Equal(MessageTemplateKind.BillingReminder, Assert.Single(rig.Repository.Rows).Kind);
        Assert.Equal(1, rig.UnitOfWork.Saves);

        rig.Bed.Meta.CreatedTemplates.Clear();
        var again = await rig.ProvisionAsync();

        Assert.Equal(["qep_cobro_vencido"], rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Equal(2, again.Count);
        Assert.Equal(2, rig.Repository.Rows.Count);
        Assert.Equal(2, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task WhenMetaRejectsTheFirstNothingIsSavedAndTheMessageNamesTheReminder()
    {
        var rig = new Rig();
        rig.Bed.Meta.CreateResults.Enqueue(new(null, new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "El cuerpo no sirve")));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, error.Code);
        Assert.StartsWith("Recordatorio: ", error.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Repository.Rows);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    // Una fila viva se ignora con todo y su cuerpo: un cliente que reenvía el texto viejo de una fila POSITIONAL (`{{1}}`) no frena la otra.
    [Fact]
    public async Task ALiveRowsBodyIsIgnoredBeforeValidatingSoItDoesNotBlockTheOther()
    {
        var rig = new Rig();
        var live = MessageTemplate.Pending(
            rig.Bed.TenantId, rig.Bed.ConnectionId, "771", "qep_cobro_recordatorio", "es", "UTILITY", "POSITIONAL", "PENDING", "[]",
            MessageTemplateKind.BillingReminder, rig.Bed.MemberId, rig.Bed.Now.AddDays(-1));
        rig.Repository.Rows.Add(live);

        var rows = await rig.ProvisionAsync(reminderBody: "Hola {{1}}, tu servicio vence.", overdueBody: OverdueBody);

        Assert.Equal([OverdueBody], rig.Bed.Meta.CreatedTemplates.Select(Rig.BodyOf));
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnotherErrorKeepsItsCodeWithoutThePrefix()
    {
        var rig = new Rig();
        rig.Bed.Meta.CreateResults.Enqueue(new(new CreatedTemplate("meta-1", "PENDING", "UTILITY"), null));
        rig.Bed.Meta.CreateResults.Enqueue(new(null, new MessagingGraphFailure(400, 100, "http_400", "Template Limit Exceeded", "x")));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateLimitReached, error.Code);
        Assert.Equal("The WhatsApp Business account reached its template limit.", error.Message);
        Assert.Equal(MessageTemplateKind.BillingReminder, Assert.Single(rig.Repository.Rows).Kind);
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
    public async Task ALiveTemplateIgnoresItsBodyAndIsNotRecreated()
    {
        var rig = new Rig();
        await rig.ProvisionAsync();
        rig.Bed.Meta.CreatedTemplates.Clear();
        var saves = rig.UnitOfWork.Saves;

        var again = await rig.ProvisionAsync(ReminderBody, OverdueBody);

        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Equal(saves, rig.UnitOfWork.Saves);
        Assert.Equal([BillingTemplateDefinitions.Reminder.BaseBody, BillingTemplateDefinitions.Overdue.BaseBody], again.Select(template => template.Body));
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
