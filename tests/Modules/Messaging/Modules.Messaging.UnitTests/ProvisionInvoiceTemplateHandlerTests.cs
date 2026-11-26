using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-21: el aprovisionamiento de la plantilla de factura, calcado del de pedido. La versión siguiente sale del
/// nombre <c>qep_factura*</c> más alto del espejo; <c>name_taken</c> salta de versión; una externa <c>qep_factura</c> viva y compatible se
/// adopta sin tocar Meta; una incompatible es <c>invoice_conflict</c>; la fila viva es <c>invoice_exists</c>; y la auditoría es
/// <c>TemplateInvoiceProvisioned</c>.</summary>
public sealed class ProvisionInvoiceTemplateHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessagingGraphFailure NameTaken = new(400, 100, "http_400", "Message Template Name Already Exists", "x");

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public RecordingLogger<ProvisionInvoiceTemplateHandler> Logger { get; } = new();

        public ProvisionInvoiceTemplateHandler Handler()
        {
            var membership = new FakeMembershipDirectory();
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            return new ProvisionInvoiceTemplateHandler(
                Repository, Bed.Meta, Bed.Connections, new FakeMemberNames(Bed.MemberId), new CallerMembership(membership, executionContext), UnitOfWork, Audit,
                Bed.TenantModules, executionContext, new FakeClock(Bed.Now), Logger, new ProvisionInvoiceTemplateCommandValidator());
        }

        public Task<ManagedTemplateDto> ProvisionAsync(string? body = null) =>
            Handler().HandleAsync(new ProvisionInvoiceTemplateCommand(Bed.TenantId, Bed.ConnectionId, body), Ct);

        public static string DocumentComponents(string body) =>
            $$"""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"{{body}}"}]""";

        public void ExternalWithBody(string body, string status = MessageTemplateStatuses.Approved) =>
            External(MessageTemplateName.InvoiceName, components: DocumentComponents(body), status: status);

        public MessageTemplate Invoice(string status, string name = MessageTemplateName.InvoiceName, string metaId = "771")
        {
            var row = MessageTemplate.Pending(
                Bed.TenantId, Bed.ConnectionId, metaId, name, "es", "UTILITY", TemplateComponentsBuilder.Named, "PENDING", "[]", MessageTemplateKind.Invoice,
                Bed.MemberId, Bed.Now.AddDays(-1));
            if (status != MessageTemplateStatuses.Pending)
            {
                row.ApplyStatus(status, "INVALID_FORMAT", Bed.Now.AddDays(-1));
            }

            Repository.Rows.Add(row);
            return row;
        }

        public void External(string name, string language = "es", string metaId = "900", string components = "[]", string status = MessageTemplateStatuses.Approved) =>
            Repository.Rows.Add(MessageTemplate.Mirrored(Bed.TenantId, Bed.ConnectionId, metaId, name, language, "UTILITY", "NAMED", status, null, components, Bed.Now.AddDays(-2)));
    }

    [Fact]
    public async Task WithoutBodyTheBaseTextIsCreatedAsAPendingInvoiceRowWithItsAudit()
    {
        var rig = new Rig();

        var dto = await rig.ProvisionAsync();

        Assert.Equal(("qep_factura", "Invoice", InvoiceTemplateDefinition.BaseBody), (dto.Name, dto.Kind.ToString(), dto.Body));
        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal((MessageTemplateKind.Invoice, MessageTemplateStatuses.Pending, "NAMED"), (row.Kind, row.Status, row.ParameterFormat));
        var upload = Assert.Single(rig.Bed.Meta.ExampleUploads);
        Assert.Equal("factura-ejemplo.pdf", upload.FileName);
        var audited = Assert.Single(rig.Audit.Templates);
        Assert.Equal((MessagingAuditActions.TemplateInvoiceProvisioned, row.Id, (IReadOnlyCollection<string>?)null), (audited.Action, audited.TemplateId, audited.ChangedFields));
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheEditedBodyGoesToMetaAndToTheRowTrimmed()
    {
        var rig = new Rig();

        var dto = await rig.ProvisionAsync("  Hola {{nombre_contacto}}, tu factura {{numero_factura}} va por {{valor_total}}.  ");

        Assert.Equal("Hola {{nombre_contacto}}, tu factura {{numero_factura}} va por {{valor_total}}.", dto.Body);
        var sent = rig.Bed.Meta.CreatedTemplates.Single()["components"]!.AsArray();
        Assert.Equal("Hola {{nombre_contacto}}, tu factura {{numero_factura}} va por {{valor_total}}.", sent.Single(component => component!["type"]!.GetValue<string>() == "BODY")!["text"]!.GetValue<string>());
    }

    // La factura no tiene vigencia ni el número del pedido; una variable de la cotización o del pedido no pasa.
    [Theory]
    [InlineData("Hola {{otra}}, gracias.")]
    [InlineData("Hola {{nombre_contacto}}, vigente hasta {{vigencia}}.")]
    [InlineData("Hola {{nombre_contacto}}, cotización {{numero_cotizacion}}.")]
    [InlineData("Hola {{nombre_contacto}}, pedido {{numero_pedido}}.")]
    public async Task AVariableOutsideTheInvoiceListIsAValidationErrorWithoutTouchingMeta(string body)
    {
        var rig = new Rig();

        var error = await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => rig.ProvisionAsync(body));

        Assert.Equal("body", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(rig.Bed.Meta.ExampleUploads);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Empty(rig.Repository.Rows);
    }

    [Theory]
    [InlineData(MessageTemplateStatuses.Pending)]
    [InlineData(MessageTemplateStatuses.Approved)]
    [InlineData(MessageTemplateStatuses.InAppeal)]
    public async Task ALiveInvoiceRowIsInvoiceExists(string status)
    {
        var rig = new Rig();
        rig.Invoice(status);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateInvoiceExists, error.Code);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
    }

    [Fact]
    public async Task AnExternalWithTheSubmittedBodyIsAdoptedWithTheKindAsTheOnlyChangedField()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, gracias.");

        var dto = await rig.ProvisionAsync("Hola {{nombre_contacto}}, gracias.");

        Assert.Equal("Invoice", dto.Kind.ToString());
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Empty(rig.Bed.Meta.ExampleUploads);
        var audited = Assert.Single(rig.Audit.Templates);
        Assert.Equal(MessagingAuditActions.TemplateInvoiceProvisioned, audited.Action);
        Assert.Equal(["kind"], audited.ChangedFields!);
    }

    [Fact]
    public async Task AnExternalWithAnotherBodyIsAnInvoiceConflict()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, gracias.");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync("Otro texto {{nombre_contacto}}."));

        Assert.Equal(MessagingErrorCodes.TemplateInvoiceConflict, error.Code);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
    }

    // Una externa con la forma de la cotización (con vigencia) no sirve de factura.
    [Fact]
    public async Task AnExternalWithTheQuotationShapeIsAnInvoiceConflictEvenWithoutBody()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, vigente hasta {{vigencia}}.");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateInvoiceConflict, error.Code);
    }

    [Fact]
    public async Task ADeadExternalIsNotAdoptedAndTheNextVersionIsCreated()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, gracias.", MessageTemplateStatuses.PendingDeletion);

        var dto = await rig.ProvisionAsync();

        Assert.Equal("qep_factura_v2", dto.Name);
        Assert.Equal(MessageTemplateKind.External, Assert.Single(rig.Repository.Rows, row => row.Name == MessageTemplateName.InvoiceName).Kind);
    }

    // La versión cuenta sólo los nombres de factura: una qep_cotizacion_v7 del mismo espejo no empuja la de la factura.
    [Fact]
    public async Task TheNextVersionComesAfterTheHighestInvoiceNameInTheMirrorAndTheRejectedRowIsReplaced()
    {
        var rig = new Rig();
        rig.Invoice(MessageTemplateStatuses.Rejected);
        rig.External("qep_factura_v3");
        rig.External("qep_cotizacion_v7", metaId: "901");
        rig.Bed.Meta.NextCreatedTemplate = new(new CreatedTemplate("772", "PENDING", "UTILITY"), null);

        var created = await rig.ProvisionAsync();

        Assert.Equal("qep_factura_v4", created.Name);
        Assert.Equal([("771", "qep_factura")], rig.Bed.Meta.Deleted);
        var invoice = Assert.Single(rig.Repository.Rows, row => row.Kind == MessageTemplateKind.Invoice);
        Assert.Equal(("772", "qep_factura_v4", MessageTemplateStatuses.Pending), (invoice.MetaTemplateId, invoice.Name, invoice.Status));
    }

    [Fact]
    public async Task ANameTakenMovesToTheNextVersionWithTheSameHandle()
    {
        var rig = new Rig();
        rig.Bed.Meta.CreateResults.Enqueue(new(null, NameTaken));
        rig.Bed.Meta.CreateResults.Enqueue(new(new CreatedTemplate("772", "PENDING", "UTILITY"), null));

        var created = await rig.ProvisionAsync();

        Assert.Equal("qep_factura_v2", created.Name);
        Assert.Equal(["qep_factura", "qep_factura_v2"], rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Single(rig.Bed.Meta.ExampleUploads);
    }

    [Fact]
    public async Task AnUploadFailureIsUploadFailedAndWritesNothing()
    {
        var rig = new Rig();
        rig.Bed.Meta.NextHandle = new(null, new MessagingGraphFailure(500, null, "http_500"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateUploadFailed, error.Code);
        Assert.Empty(rig.Bed.Connections.Reported);
        Assert.Empty(rig.Repository.Rows);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }
}
