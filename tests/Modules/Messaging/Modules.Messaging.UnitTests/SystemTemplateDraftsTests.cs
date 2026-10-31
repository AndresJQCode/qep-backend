using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Los borradores que abre la ventana de edición: el texto inicial y si se puede editar salen resueltos del espejo, para que la
/// pantalla no conozca el formato de Meta ni las reglas de adopción.</summary>
public sealed class SystemTemplateDraftsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        private FakeExecutionContext Context => new(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);

        public Task<SystemTemplateDraftDto> QuotationAsync() =>
            new GetQuotationTemplateDraftHandler(Repository, Bed.Connections, Bed.TenantModules, Context)
                .HandleAsync(new GetQuotationTemplateDraftQuery(Bed.TenantId, Bed.ConnectionId), Ct);

        public Task<BillingTemplatesDraftDto> BillingAsync() =>
            new GetBillingTemplatesDraftHandler(Repository, Bed.Connections, Bed.TenantModules, Context)
                .HandleAsync(new GetBillingTemplatesDraftQuery(Bed.TenantId, Bed.ConnectionId), Ct);

        public MessageTemplate Row(
            MessageTemplateKind kind, string name, string format, string text, string status = "PENDING", string? reason = null)
        {
            var row = MessageTemplate.Pending(
                Bed.TenantId, Bed.ConnectionId, "771", name, "es", "UTILITY", format, "PENDING",
                $$"""[{"type":"BODY","text":"{{text}}"}]""", kind, Bed.MemberId, Bed.Now.AddDays(-1));
            if (status != "PENDING")
            {
                row.ApplyStatus(status, reason, Bed.Now.AddDays(-1));
            }

            Repository.Rows.Add(row);
            return row;
        }

        public void External(string components) =>
            Repository.Rows.Add(MessageTemplate.Mirrored(
                Bed.TenantId, Bed.ConnectionId, "900", QuotationTemplateDefinition.Name, "es", "UTILITY", "NAMED", "APPROVED", null, components, Bed.Now.AddDays(-2)));
    }

    [Fact]
    public async Task WithoutRowsTheDraftIsTheBaseTextWithItsList()
    {
        var draft = await new Rig().QuotationAsync();

        Assert.Equal(QuotationTemplateDefinition.BaseBody, draft.Body);
        Assert.False(draft.Locked);
        Assert.Null(draft.RejectionReason);
        Assert.Equal(7, draft.Variables.Count);
        Assert.Equal(("nombre_contacto", "Nombre del contacto", "María González"), (draft.Variables[0].Key, draft.Variables[0].Label, draft.Variables[0].Example));
    }

    [Fact]
    public async Task ARejectedRowStartsFromItsBodyAndCarriesTheReason()
    {
        var rig = new Rig();
        rig.Row(MessageTemplateKind.Quotation, "qep_cotizacion", "NAMED", "Hola {{nombre_contacto}}, tu cotización.", MessageTemplateStatuses.Rejected, "INVALID_FORMAT");

        var draft = await rig.QuotationAsync();

        Assert.Equal("Hola {{nombre_contacto}}, tu cotización.", draft.Body);
        Assert.False(draft.Locked);
        Assert.Equal("INVALID_FORMAT", draft.RejectionReason);
    }

    [Fact]
    public async Task ACompatibleExternalStartsFromItsBody()
    {
        var rig = new Rig();
        rig.External("""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"Hola {{nombre_contacto}}, adjunto."}]""");

        var draft = await rig.QuotationAsync();

        Assert.Equal("Hola {{nombre_contacto}}, adjunto.", draft.Body);
        Assert.False(draft.Locked);
        Assert.Null(draft.RejectionReason);
    }

    [Fact]
    public async Task AnIncompatibleExternalStartsFromTheBaseText()
    {
        var rig = new Rig();
        rig.External("""[{"type":"BODY","text":"Sin encabezado de documento."}]""");

        var draft = await rig.QuotationAsync();

        Assert.Equal(QuotationTemplateDefinition.BaseBody, draft.Body);
        Assert.False(draft.Locked);
    }

    // Su texto no pasaría el validador: partir de él obligaría al admin a borrar todo.
    [Fact]
    public async Task ARejectedQuotationWithTheOldKeysStartsFromTheBaseText()
    {
        var rig = new Rig();
        rig.Row(MessageTemplateKind.Quotation, "qep_cotizacion", "NAMED", "Hola {{fullname}}, tu cotización {{order_number}}.", MessageTemplateStatuses.Rejected, "INVALID_FORMAT");

        var draft = await rig.QuotationAsync();

        Assert.Equal(QuotationTemplateDefinition.BaseBody, draft.Body);
        Assert.False(draft.Locked);
        Assert.Equal("INVALID_FORMAT", draft.RejectionReason);
    }

    [Fact]
    public async Task ARejectedPositionalBillingRowStartsFromTheBaseText()
    {
        var rig = new Rig();
        rig.Row(MessageTemplateKind.BillingOverdue, "qep_cobro_vencido", "POSITIONAL", "Hola {{1}}, tienes cobros vencidos.", MessageTemplateStatuses.Rejected, "INVALID_FORMAT");

        var draft = await rig.BillingAsync();

        Assert.Equal(BillingTemplateDefinitions.Overdue.BaseBody, draft.Overdue.Body);
        Assert.False(draft.Overdue.Locked);
        Assert.Equal("INVALID_FORMAT", draft.Overdue.RejectionReason);
        Assert.Equal(BillingTemplateDefinitions.Reminder.BaseBody, draft.Reminder.Body);
    }

    [Fact]
    public async Task ALiveBillingRowIsLockedWithItsBody()
    {
        var rig = new Rig();
        rig.Row(MessageTemplateKind.BillingReminder, "qep_cobro_recordatorio", "POSITIONAL", "Hola {{1}}, tu servicio vence.", MessageTemplateStatuses.Approved);

        var draft = await rig.BillingAsync();

        Assert.True(draft.Reminder.Locked);
        Assert.Equal("Hola {{1}}, tu servicio vence.", draft.Reminder.Body);
        Assert.False(draft.Overdue.Locked);
        Assert.Equal(BillingTemplateDefinitions.Overdue.BaseBody, draft.Overdue.Body);
        Assert.Equal(7, draft.Reminder.Variables.Count);
        Assert.Equal(6, draft.Overdue.Variables.Count);
    }

    [Fact]
    public async Task BillingDraftNeedsTheModule()
    {
        var rig = new Rig();
        rig.Bed.TenantModules.Sets[rig.Bed.TenantId] = TenantModuleSet.Empty;

        var error = await Assert.ThrowsAsync<BuildingBlocks.Application.RequestForbiddenException>(() => rig.BillingAsync());

        Assert.Equal("tenancy.module_not_enabled", error.Code);
    }
}
