using System.Reflection;
using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Audit.Domain;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// El cambio de cuenta de Siigo al editar la conexión: se compara sólo por ids contra las listas de la cuenta nueva, y si no cuadran
/// no se guarda nada sin confirmación; con ella, la configuración vuelve a empezar y el cambio queda auditado.
/// </summary>
public sealed class SiigoAccountChangeTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly SiigoCredentials NewAccount = new(ConnectionId, "nueva@empresa.test", "clave-nueva");
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeSettings _settings = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly RecordingAudit _audit = new();
    private readonly ListsApi _api = ListsApi.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SiigoAccountChange Service() => new(_api.Instance, _settings, _unitOfWork, _audit, new Context(), new FixedClock());

    private SiigoSettings Saved(
        string? documentType = "doc-1", string? payment = "pay-1", string? credit = null, string? cod = null, string? fallback = null)
    {
        var settings = SiigoSettings.Create(ConnectionId, TenantId);
        settings.UpdateDocument(new SiigoDocumentSettings(documentType, null, null, false, false, false, null));
        settings.UpdatePayments(new SiigoPaymentSettings([new SiigoPaymentLink("BANCO|1", payment)], credit, null, cod, fallback));
        _settings.Stored = settings;
        return settings;
    }

    [Fact]
    public async Task WithoutASavedRowThereIsNothingToCompareAndNoListIsRead()
    {
        var must = await Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: false, Ct);

        Assert.False(must);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task WithNoSavedIdsThereIsNothingToCompareAndNoListIsRead()
    {
        Saved(documentType: null, payment: null);

        var must = await Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: false, Ct);

        Assert.False(must);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task TheSameAccountWithARotatedKeyKeepsEverything()
    {
        Saved(credit: "pay-2", cod: "pay-1", fallback: "pay-2");
        _api.DocumentTypes = [new SiigoOption("doc-1", "FV")];
        _api.PaymentTypes = [new SiigoOption("pay-1", "Efectivo"), new SiigoOption("pay-2", "Crédito")];

        var must = await Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: false, Ct);

        Assert.False(must);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ASavedDocumentTypeMissingFromTheNewAccountIsAnUnconfirmedConflictWithItsId()
    {
        Saved();
        _api.DocumentTypes = [new SiigoOption("otro", "FV")];
        _api.PaymentTypes = [new SiigoOption("pay-1", "Efectivo")];

        var error = await Assert.ThrowsAsync<SiigoAccountChangedException>(
            () => Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: false, Ct));

        Assert.Equal(SiigoErrorCodes.ConnectionAccountChanged, error.Code);
        var mismatch = Assert.Single(error.Mismatches);
        Assert.Equal(new SiigoAccountMismatch("documentType", "doc-1"), mismatch);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task EverySavedPaymentTypeMissingFromTheNewAccountIsReportedOnceAndNullsAreSkipped()
    {
        Saved(payment: "pay-1", credit: "pay-9", cod: "pay-1", fallback: null);
        _api.DocumentTypes = [new SiigoOption("doc-1", "FV")];
        _api.PaymentTypes = [new SiigoOption("pay-7", "Otra")];

        var error = await Assert.ThrowsAsync<SiigoAccountChangedException>(
            () => Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: false, Ct));

        Assert.Equal(
            [new SiigoAccountMismatch("paymentType", "pay-1"), new SiigoAccountMismatch("paymentType", "pay-9")],
            error.Mismatches);
    }

    [Fact]
    public void TheProblemExtensionsCarryOnlyKindAndIdOfEachMismatch()
    {
        var error = new SiigoAccountChangedException([new SiigoAccountMismatch("documentType", "doc-1")]);

        var json = JsonSerializer.Serialize(error.ProblemExtensions, JsonSerializerOptions.Web);

        Assert.Equal("""{"mismatches":[{"kind":"documentType","id":"doc-1"}]}""", json);
    }

    [Fact]
    public async Task ConfirmedWithAMismatchAsksForTheReset()
    {
        Saved();
        _api.DocumentTypes = [new SiigoOption("otro", "FV")];
        _api.PaymentTypes = [new SiigoOption("pay-1", "Efectivo")];

        var must = await Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: true, Ct);

        Assert.True(must);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task ConfirmedWithoutAMismatchIsIgnored()
    {
        Saved();
        _api.DocumentTypes = [new SiigoOption("doc-1", "FV")];
        _api.PaymentTypes = [new SiigoOption("pay-1", "Efectivo")];

        Assert.False(await Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: true, Ct));
    }

    [Fact]
    public async Task OnlyTheListsOfTheSavedKindsAreRead()
    {
        Saved(payment: null);
        _api.DocumentTypes = [new SiigoOption("doc-1", "FV")];

        await Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: false, Ct);

        Assert.Equal(["ListDocumentTypesAsync"], _api.Methods);
    }

    [Fact]
    public async Task ATransientFailureReadingTheListsReachesTheCallerUntouched()
    {
        Saved();
        _api.Failure = new ServiceUnavailableException("siigo.unavailable", "Siigo is down.");

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => Service().MustResetAsync(TenantId, ConnectionId, NewAccount, confirmed: true, Ct));
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task TheResetClearsTheConfigurationSavesItOnceAndAuditsItWithoutValues()
    {
        var settings = Saved(credit: "pay-2");
        var before = settings.Version;

        await Service().ResetAsync(TenantId, ConnectionId, Ct);

        Assert.Null(settings.DocumentTypeId);
        Assert.Empty(settings.PaymentLinks);
        Assert.Equal(before + 1, settings.Version);
        Assert.Equal(1, _unitOfWork.Saves);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(SiigoAuditActions.SettingsAccountReset, entry.Action);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Equal(ConnectionId.ToString(), entry.ResourceId);
        Assert.Equal("""["account_reset"]""", entry.ChangedFieldsJson);
        Assert.Equal(Now, entry.OccurredAt);
    }

    private sealed class FakeSettings : ISiigoSettingsRepository
    {
        public SiigoSettings? Stored { get; set; }

        public Task<SiigoSettings?> FindAsync(Guid tenantId, Guid connectionId, bool withVariantLinks, CancellationToken cancellationToken) =>
            Task.FromResult(Stored is { } stored && stored.TenantId == tenantId && stored.ConnectionId == connectionId ? Stored : null);

        public Task<SiigoSettings?> FindOrCreateAsync(
            Guid tenantId, Guid connectionId, bool withVariantLinks, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiigoSettings?> FindByCompanyAsync(
            Guid tenantId, Guid companyId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Remove(SiigoSettings settings) => throw new NotSupportedException();
    }

    private sealed class FakeUnitOfWork : ISiigoUnitOfWork
    {
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(++Saves);

        public Task<ISiigoTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task LockOrderAsync(Guid orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed record AuditEntry(Guid TenantId, string Action, string ResourceId, string ChangedFieldsJson, DateTimeOffset OccurredAt);

    private sealed class RecordingAudit : ISiigoAuditRecorder
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(
            Guid tenantId, Guid actorId, AuditActorType actorType, string action, string resourceType, string resourceId,
            string changedFieldsJson, DateTimeOffset occurredAt) =>
            Entries.Add(new AuditEntry(tenantId, action, resourceId, changedFieldsJson, occurredAt));
    }

    private sealed class Context : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(SiigoAccountChangeTests.TenantId);

        public bool HasPermission(string permission) => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>Un <see cref="ISiigoApi"/> que sólo sabe listar comprobantes y formas de pago, y cuenta las llamadas.</summary>
    public class ListsApi : DispatchProxy
    {
        public IReadOnlyList<SiigoOption> DocumentTypes { get; set; } = [];

        public IReadOnlyList<SiigoOption> PaymentTypes { get; set; } = [];

        public Exception? Failure { get; set; }

        public List<string> Methods { get; } = [];

        public int Calls => Methods.Count;

        public ISiigoApi Instance => (ISiigoApi)(object)this;

        public static ListsApi Create() => (ListsApi)(object)DispatchProxy.Create<ISiigoApi, ListsApi>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Methods.Add(targetMethod!.Name);
            if (Failure is { } failure)
            {
                throw failure;
            }

            return targetMethod.Name switch
            {
                nameof(ISiigoApi.ListDocumentTypesAsync) => Task.FromResult(DocumentTypes),
                nameof(ISiigoApi.ListPaymentTypesAsync) => Task.FromResult(PaymentTypes),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }
    }
}
