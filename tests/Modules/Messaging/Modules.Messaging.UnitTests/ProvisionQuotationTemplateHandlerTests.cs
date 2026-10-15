using Microsoft.Extensions.Logging;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.5 con el reintento versionado (enmienda del owner, 2026-10-15): lo que las pruebas de integración no alcanzan
/// sin forzar a Meta. La versión siguiente sale del nombre reservado más alto del espejo (cualquier kind, sólo <c>es</c>); <c>name_taken</c>
/// salta de versión con el mismo handle hasta cinco creaciones; el borrado de la vieja va después de crear, y si falla por otra cosa que el
/// token sólo deja un warning sin el token.</summary>
public sealed class ProvisionQuotationTemplateHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly MessagingGraphFailure NameTaken = new(400, 100, "http_400", "Message Template Name Already Exists", "x");

    private sealed class Rig
    {
        public MessagingTestBed Bed { get; } = new();

        public FakeMessageTemplateRepository Repository { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public RecordingLogger<ProvisionQuotationTemplateHandler> Logger { get; } = new();

        public ProvisionQuotationTemplateHandler Handler()
        {
            var membership = new FakeMembershipDirectory();
            membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
            var executionContext = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, MessagingPermissions.TemplateManage);
            return new ProvisionQuotationTemplateHandler(
                Repository, Bed.Meta, Bed.Connections, new FakeMemberNames(Bed.MemberId), new CallerMembership(membership, executionContext), UnitOfWork, Audit,
                Bed.TenantModules, executionContext, new FakeClock(Bed.Now), Logger);
        }

        public Task<ManagedTemplateDto> ProvisionAsync() => Handler().HandleAsync(new ProvisionQuotationTemplateCommand(Bed.TenantId, Bed.ConnectionId), Ct);

        /// <summary>La <c>Quotation</c> que Meta rechazó: lo que el admin reintenta.</summary>
        public MessageTemplate RejectedQuotation(string name = MessageTemplateName.QuotationName, string metaId = "771")
        {
            var row = MessageTemplate.Pending(
                Bed.TenantId, Bed.ConnectionId, metaId, name, "es", "UTILITY", TemplateComponentsBuilder.Named, "PENDING", "[]", MessageTemplateKind.Quotation,
                Bed.MemberId, Bed.Now.AddDays(-1));
            row.ApplyStatus(MessageTemplateStatuses.Rejected, "INVALID_FORMAT", Bed.Now.AddDays(-1));
            Repository.Rows.Add(row);
            return row;
        }

        public void External(string name, string language = "es", string metaId = "900") =>
            Repository.Rows.Add(MessageTemplate.Mirrored(Bed.TenantId, Bed.ConnectionId, metaId, name, language, "UTILITY", "NAMED", "APPROVED", null, "[]", Bed.Now.AddDays(-2)));
    }

    [Fact]
    public async Task TheNextVersionComesAfterTheHighestReservedSpanishNameInTheMirrorOfAnyKind()
    {
        var rig = new Rig();
        rig.RejectedQuotation();
        rig.External("qep_cotizacion_v3");
        rig.External("qep_cotizacion_v7", language: "en", metaId: "901");
        rig.External("qep_cotizaciones", metaId: "902");
        rig.Bed.Meta.NextCreatedTemplate = new(new CreatedTemplate("772", "PENDING", "UTILITY"), null);

        var created = await rig.ProvisionAsync();

        Assert.Equal("qep_cotizacion_v4", created.Name);
        Assert.Equal(["qep_cotizacion_v4"], rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Equal([("771", "qep_cotizacion")], rig.Bed.Meta.Deleted);
        var quotation = Assert.Single(rig.Repository.Rows, row => row.Kind == MessageTemplateKind.Quotation);
        Assert.Equal(("772", "qep_cotizacion_v4", MessageTemplateStatuses.Pending), (quotation.MetaTemplateId, quotation.Name, quotation.Status));
        var audited = Assert.Single(rig.Audit.Templates);
        Assert.Equal((MessagingAuditActions.TemplateQuotationProvisioned, quotation.Id, (IReadOnlyCollection<string>?)null), (audited.Action, audited.TemplateId, audited.ChangedFields));
        Assert.Equal(1, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task FiveNameTakenInARowSurfaceNameTakenAfterFiveCreatesWithASingleUploadAndWriteNothing()
    {
        var rig = new Rig();
        rig.Bed.Meta.NextCreatedTemplate = new(null, NameTaken);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(rig.ProvisionAsync);

        Assert.Equal(MessagingErrorCodes.TemplateNameTaken, error.Code);
        Assert.Equal(
            ["qep_cotizacion", "qep_cotizacion_v2", "qep_cotizacion_v3", "qep_cotizacion_v4", "qep_cotizacion_v5"],
            rig.Bed.Meta.CreatedTemplates.Select(payload => payload["name"]!.GetValue<string>()));
        Assert.Single(rig.Bed.Meta.ExampleUploads);
        Assert.All(rig.Bed.Meta.CreatedTemplates, payload =>
            Assert.Equal("4::handle", payload["components"]![0]!["example"]!["header_handle"]![0]!.GetValue<string>()));
        Assert.Empty(rig.Repository.Rows);
        Assert.Empty(rig.Audit.Templates);
        Assert.Equal(0, rig.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AnotherCreateFailureIsNotRetriedWithTheNextVersion()
    {
        var rig = new Rig();
        rig.Bed.Meta.NextCreatedTemplate = new(null, new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "The body ends with a variable."));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(rig.ProvisionAsync);

        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, error.Code);
        Assert.Single(rig.Bed.Meta.CreatedTemplates);
    }

    /// <summary>Revisión final F1: la subida le pega a <c>/{AppId}/uploads</c>, un endpoint de la app y no de la WABA. Ninguna falla suya
    /// —ni un 401, ni un <c>code</c> 190 o de permiso— dice nada del token de la conexión: es <c>upload_failed</c> y la conexión no se reporta.</summary>
    [Theory]
    [InlineData(401, 190, "http_401")]
    [InlineData(403, 10, "http_403")]
    [InlineData(400, 200, "http_400")]
    [InlineData(500, null, "http_500")]
    [InlineData(0, null, "no_app_id")]
    [InlineData(200, null, "no_handle")]
    public async Task AnyUploadFailureIsUploadFailedWithAWarningAndNeverReportsTheConnection(int status, int? code, string reason)
    {
        var rig = new Rig();
        rig.Bed.Meta.NextHandle = new(null, new MessagingGraphFailure(status, code, reason));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(rig.ProvisionAsync);

        Assert.Equal(MessagingErrorCodes.TemplateUploadFailed, error.Code);
        Assert.Empty(rig.Bed.Connections.Reported);
        Assert.Empty(rig.Bed.Meta.CreatedTemplates);
        Assert.Empty(rig.Repository.Rows);
        Assert.Equal(0, rig.UnitOfWork.Saves);
        var warning = Assert.Single(rig.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(rig.Bed.TenantId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Bed.ConnectionId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains($"status {status}", warning.Message, StringComparison.Ordinal);
        Assert.Contains($"code {(code is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}", warning.Message, StringComparison.Ordinal);
        Assert.Contains(reason, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOldVersionMetaWillNotDeleteForAnotherReasonLeavesAWarningWithoutTheTokenAndTheRowIsReplacedAnyway()
    {
        var rig = new Rig();
        var rejected = rig.RejectedQuotation();
        rig.Bed.Meta.NextCreatedTemplate = new(new CreatedTemplate("772", "PENDING", "UTILITY"), null);
        rig.Bed.Meta.NextDelete = new(null, new MessagingGraphFailure(500, null, "http_500"));

        var created = await rig.ProvisionAsync();

        Assert.Equal("qep_cotizacion_v2", created.Name);
        Assert.DoesNotContain(rejected, rig.Repository.Rows);
        Assert.Equal("772", Assert.Single(rig.Repository.Rows).MetaTemplateId);
        Assert.Equal(1, rig.UnitOfWork.Saves);
        var warning = Assert.Single(rig.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(rig.Bed.TenantId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Bed.ConnectionId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains("771", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Bed.Connections.Reported);
    }

    [Fact]
    public async Task AnOldVersionDeleteRejectedForTheTokenIsConnectionUnavailableAfterCreatingAndWritesNothing()
    {
        var rig = new Rig();
        var rejected = rig.RejectedQuotation();
        rig.Bed.Meta.NextCreatedTemplate = new(new CreatedTemplate("772", "PENDING", "UTILITY"), null);
        rig.Bed.Meta.NextDelete = new(null, new MessagingGraphFailure(401, 190, "http_401"));

        var error = await Assert.ThrowsAsync<MessagingDomainException>(rig.ProvisionAsync);

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Single(rig.Bed.Meta.CreatedTemplates);
        Assert.Equal([rejected], rig.Repository.Rows);
        Assert.Equal(MessageTemplateStatuses.Rejected, rejected.Status);
        Assert.Equal(0, rig.UnitOfWork.Saves);
        Assert.Single(rig.Bed.Connections.Reported);
    }
}

/// <summary>Anota cada entrada ya formateada; sirve para comprobar qué deja un warning y qué no.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
