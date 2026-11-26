using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-09 D3: el aprovisionamiento de la plantilla de pedido, calcado del de cotización. La versión siguiente sale del
/// nombre <c>qep_pedido*</c> más alto del espejo; <c>name_taken</c> salta de versión; una externa <c>qep_pedido</c> viva y compatible se
/// adopta sin tocar Meta; una incompatible es <c>order_conflict</c>; la fila viva es <c>order_exists</c>; y la auditoría es
/// <c>TemplateOrderProvisioned</c>.</summary>
public sealed class ProvisionOrderTemplateHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessagingGraphFailure NameTaken = new(400, 100, "http_400", "Message Template Name Already Exists", "x");

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public RecordingLogger<ProvisionOrderTemplateHandler> Logger { get; } = new();

        public ProvisionOrderTemplateHandler Handler()
        {
            var membership = new FakeMembershipDirectory();
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            return new ProvisionOrderTemplateHandler(
                Repository, Bed.Meta, Bed.Connections, new FakeMemberNames(Bed.MemberId), new CallerMembership(membership, executionContext), UnitOfWork, Audit,
                Bed.TenantModules, executionContext, new FakeClock(Bed.Now), Logger, new ProvisionOrderTemplateCommandValidator());
        }

        public Task<ManagedTemplateDto> ProvisionAsync(string? body = null) =>
            Handler().HandleAsync(new ProvisionOrderTemplateCommand(Bed.TenantId, Bed.ConnectionId, body), Ct);

        public static string DocumentComponents(string body) =>
            $$"""[{"type":"HEADER","format":"DOCUMENT"},{"type":"BODY","text":"{{body}}"}]""";

        public void ExternalWithBody(string body, string status = MessageTemplateStatuses.Approved) =>
            External(MessageTemplateName.OrderName, components: DocumentComponents(body), status: status);

        public MessageTemplate Order(string status, string name = MessageTemplateName.OrderName, string metaId = "771")
        {
            var row = MessageTemplate.Pending(
                Bed.TenantId, Bed.ConnectionId, metaId, name, "es", "UTILITY", TemplateComponentsBuilder.Named, "PENDING", "[]", MessageTemplateKind.Order,
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
    public async Task WithoutBodyTheBaseTextIsCreatedAsAPendingOrderRowWithItsAudit()
    {
        var rig = new Rig();

        var dto = await rig.ProvisionAsync();

        Assert.Equal(("qep_pedido", "Order", OrderTemplateDefinition.BaseBody), (dto.Name, dto.Kind.ToString(), dto.Body));
        var row = Assert.Single(rig.Repository.Rows);
        Assert.Equal((MessageTemplateKind.Order, MessageTemplateStatuses.Pending, "NAMED"), (row.Kind, row.Status, row.ParameterFormat));
        var upload = Assert.Single(rig.Bed.Meta.ExampleUploads);
        Assert.Equal("pedido-ejemplo.pdf", upload.FileName);
        var audited = Assert.Single(rig.Audit.Templates);
        Assert.Equal((MessagingAuditActions.TemplateOrderProvisioned, row.Id, (IReadOnlyCollection<string>?)null), (audited.Action, audited.TemplateId, audited.ChangedFields));
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheEditedBodyGoesToMetaAndToTheRowTrimmed()
    {
        var rig = new Rig();

        var dto = await rig.ProvisionAsync("  Hola {{nombre_contacto}}, tu pedido {{numero_pedido}} va por {{valor_total}}.  ");

        Assert.Equal("Hola {{nombre_contacto}}, tu pedido {{numero_pedido}} va por {{valor_total}}.", dto.Body);
        var sent = rig.Bed.Meta.CreatedTemplates.Single()["components"]!.AsArray();
        Assert.Equal("Hola {{nombre_contacto}}, tu pedido {{numero_pedido}} va por {{valor_total}}.", sent.Single(component => component!["type"]!.GetValue<string>() == "BODY")!["text"]!.GetValue<string>());
    }

    // D2: el pedido no tiene vigencia; una variable de la cotización no pasa.
    [Theory]
    [InlineData("Hola {{otra}}, gracias.")]
    [InlineData("Hola {{nombre_contacto}}, vigente hasta {{vigencia}}.")]
    [InlineData("Hola {{nombre_contacto}}, cotización {{numero_cotizacion}}.")]
    public async Task AVariableOutsideTheOrderListIsAValidationErrorWithoutTouchingMeta(string body)
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
    public async Task ALiveOrderRowIsOrderExists(string status)
    {
        var rig = new Rig();
        rig.Order(status);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateOrderExists, error.Code);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
    }

    [Fact]
    public async Task AnExternalWithTheSubmittedBodyIsAdoptedWithTheKindAsTheOnlyChangedField()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, gracias.");

        var dto = await rig.ProvisionAsync("Hola {{nombre_contacto}}, gracias.");

        Assert.Equal("Order", dto.Kind.ToString());
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Empty(rig.Bed.Meta.ExampleUploads);
        var audited = Assert.Single(rig.Audit.Templates);
        Assert.Equal(MessagingAuditActions.TemplateOrderProvisioned, audited.Action);
        Assert.Equal(["kind"], audited.ChangedFields!);
    }

    [Fact]
    public async Task AnExternalWithAnotherBodyIsAnOrderConflict()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, gracias.");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync("Otro texto {{nombre_contacto}}."));

        Assert.Equal(MessagingErrorCodes.TemplateOrderConflict, error.Code);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
    }

    // Una externa con la forma de la cotización (con vigencia) no sirve de pedido.
    [Fact]
    public async Task AnExternalWithTheQuotationShapeIsAnOrderConflictEvenWithoutBody()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, vigente hasta {{vigencia}}.");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => rig.ProvisionAsync());

        Assert.Equal(MessagingErrorCodes.TemplateOrderConflict, error.Code);
    }

    [Fact]
    public async Task ADeadExternalIsNotAdoptedAndTheNextVersionIsCreated()
    {
        var rig = new Rig();
        rig.ExternalWithBody("Hola {{nombre_contacto}}, gracias.", MessageTemplateStatuses.PendingDeletion);

        var dto = await rig.ProvisionAsync();

        Assert.Equal("qep_pedido_v2", dto.Name);
        Assert.Equal(MessageTemplateKind.External, Assert.Single(rig.Repository.Rows, row => row.Name == MessageTemplateName.OrderName).Kind);
    }

    // La versión cuenta sólo los nombres de pedido: una qep_cotizacion_v7 del mismo espejo no empuja la del pedido.
    [Fact]
    public async Task TheNextVersionComesAfterTheHighestOrderNameInTheMirrorAndTheRejectedRowIsReplaced()
    {
        var rig = new Rig();
        rig.Order(MessageTemplateStatuses.Rejected);
        rig.External("qep_pedido_v3");
        rig.External("qep_cotizacion_v7", metaId: "901");
        rig.Bed.Meta.NextCreatedTemplate = new(new CreatedTemplate("772", "PENDING", "UTILITY"), null);

        var created = await rig.ProvisionAsync();

        Assert.Equal("qep_pedido_v4", created.Name);
        Assert.Equal([("771", "qep_pedido")], rig.Bed.Meta.Deleted);
        var order = Assert.Single(rig.Repository.Rows, row => row.Kind == MessageTemplateKind.Order);
        Assert.Equal(("772", "qep_pedido_v4", MessageTemplateStatuses.Pending), (order.MetaTemplateId, order.Name, order.Status));
    }

    [Fact]
    public async Task ANameTakenMovesToTheNextVersionWithTheSameHandle()
    {
        var rig = new Rig();
        rig.Bed.Meta.CreateResults.Enqueue(new(null, NameTaken));
        rig.Bed.Meta.CreateResults.Enqueue(new(new CreatedTemplate("772", "PENDING", "UTILITY"), null));

        var created = await rig.ProvisionAsync();

        Assert.Equal("qep_pedido_v2", created.Name);
        Assert.Equal(["qep_pedido", "qep_pedido_v2"], rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
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
