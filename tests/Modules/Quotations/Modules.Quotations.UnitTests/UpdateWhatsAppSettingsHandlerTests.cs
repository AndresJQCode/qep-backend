using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PUT de la configuración de WhatsApp (spec 2026-10-07, «Casos de uso»), en su orden: autoriza
/// antes de validar, gate de capacidad, formato, versión, lo requerido en Own según la fila, campos
/// propios ignorados fuera de Own, drenaje de rotación que nunca convierte un guardado en 500, un
/// Configure por guardado y una auditoría por clase de cambio.
/// </summary>
public sealed class UpdateWhatsAppSettingsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Earlier = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 22, 0, 0, TimeSpan.Zero);
    private static readonly string[] MissingOwnFields = ["ApiKey", "FromNumber", "Provider", "TemplateId"];
    private const string Key = "zenvia-token-123";
    private const string FromNumber = "573001234567";
    private const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    private sealed record Harness(
        UpdateWhatsAppSettingsHandler Handler,
        InMemoryTenantWhatsAppSettingsRepository Repository,
        FakeWhatsAppSecretProtector Protector,
        RecordingExportAuditPublisher Audit,
        CountingQuotationsUnitOfWork UnitOfWork,
        FixedTenantModules Modules);

    private static Harness NewHandler(
        IExecutionContext? executionContext = null, FixedTenantModules? modules = null)
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        var protector = new FakeWhatsAppSecretProtector();
        var audit = new RecordingExportAuditPublisher();
        var unitOfWork = new CountingQuotationsUnitOfWork();
        modules ??= FixedTenantModules.AllEnabled();
        var handler = new UpdateWhatsAppSettingsHandler(
            repository,
            unitOfWork,
            audit,
            protector,
            modules,
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now),
            new UpdateWhatsAppSettingsValidator());
        return new Harness(handler, repository, protector, audit, unitOfWork, modules);
    }

    private static UpdateWhatsAppSettingsCommand Command(
        string mode,
        long expectedVersion = 1,
        string? provider = null,
        string? apiKey = null,
        string? fromNumber = null,
        string? templateId = null) =>
        new(TenantId, mode, provider, apiKey, fromNumber, templateId, expectedVersion);

    private static UpdateWhatsAppSettingsCommand FullOwn(long expectedVersion = 1, string apiKey = Key) =>
        Command("Own", expectedVersion, "Zenvia", apiKey, FromNumber, TemplateId);

    /// <summary>Una fila Own guardada antes de la prueba, en versión 2.</summary>
    private static TenantWhatsAppSettings OwnRow(ProtectedSecret token)
    {
        var row = TenantWhatsAppSettings.CreateEmpty(TenantId, Earlier);
        row.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, token, null, FromNumber, TemplateId, Earlier);
        return row;
    }

    private static IReadOnlyList<string> Actions(Harness harness) =>
        [.. harness.Audit.Entries.Select(entry => entry.Action)];

    [Fact]
    public async Task WithoutPermissionAndAnInvalidBodyIsForbiddenNotAValidationFailure()
    {
        var harness = NewHandler(new PermissionlessExecutionContext(SubjectId, TenantId));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(Command("own", expectedVersion: 0), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
        Assert.Equal(0, harness.Modules.FindCalls);
        Assert.Equal(0, harness.Repository.FindCalls);
    }

    [Fact]
    public async Task ForAnotherTenantIsForbidden()
    {
        var harness = NewHandler(new StubExecutionContext(SubjectId, Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(Command("Disabled"), TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.Repository.FindCalls);
    }

    [Fact]
    public async Task WithoutTheQuotationsModuleIsModuleNotEnabledBeforeReadingOrValidating()
    {
        var harness = NewHandler(modules: FixedTenantModules.Without(TenantModuleKeys.Quotations));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            harness.Handler.HandleAsync(Command("own", expectedVersion: 0), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, harness.Repository.FindCalls);
    }

    // El tenant del stub de desarrollo no existe en tenancy.tenants: el guard no lanza.
    [Fact]
    public async Task AnUnknownTenantPassesTheGate()
    {
        var harness = NewHandler(modules: FixedTenantModules.Simulated);

        var dto = await harness.Handler.HandleAsync(Command("Disabled"), TestContext.Current.CancellationToken);

        Assert.Equal("Disabled", dto.Mode);
    }

    [Fact]
    public async Task OwnWithoutARowAndAnEmptyBodyListsTheFourMissingFieldsAtOnce()
    {
        var harness = NewHandler();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Handler.HandleAsync(Command("Own"), TestContext.Current.CancellationToken));

        Assert.Equal(
            MissingOwnFields,
            error.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(error.Errors, failure =>
            failure.PropertyName == "ApiKey" && failure.ErrorMessage == UpdateWhatsAppSettingsHandler.ApiKeyRequiredMessage);
        Assert.Empty(harness.Repository.Rows);
    }

    [Fact]
    public async Task BackToOwnWithEverythingStoredAndReadableNeedsOnlyTheMode()
    {
        var harness = NewHandler();
        var row = OwnRow(harness.Protector.Seed("k2", Key));
        row.Configure(WhatsAppMode.Disabled, null, null, null, null, null, Earlier);
        harness.Repository.Add(row);

        var dto = await harness.Handler.HandleAsync(Command("Own", expectedVersion: 3), TestContext.Current.CancellationToken);

        Assert.Equal("Own", dto.Mode);
        Assert.Equal(4, dto.Version);
        Assert.True(dto.ApiKeyReadable);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.ModeChangedAction}, Actions(harness));
    }

    [Fact]
    public async Task OwnWithAnUnreadableKeyAndNoNewKeyAsksToPasteItAgain()
    {
        var harness = NewHandler();
        var row = OwnRow(FakeWhatsAppSecretProtector.Garbage("k2"));
        row.Configure(WhatsAppMode.Shared, null, null, null, null, null, Earlier);
        harness.Repository.Add(row);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Handler.HandleAsync(Command("Own", expectedVersion: 3), TestContext.Current.CancellationToken));

        var failure = Assert.Single(error.Errors);
        Assert.Equal("ApiKey", failure.PropertyName);
        Assert.Equal(UpdateWhatsAppSettingsHandler.ApiKeyUnreadableMessage, failure.ErrorMessage);
    }

    [Fact]
    public async Task DisabledWithOwnFieldsInTheBodyDoesNotStoreThem()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(
            Command("Disabled", 1, "Zenvia", Key, FromNumber, TemplateId), TestContext.Current.CancellationToken);

        var row = Assert.Single(harness.Repository.Rows);
        Assert.Equal(WhatsAppMode.Disabled, row.Mode);
        Assert.Null(row.Provider);
        Assert.Null(row.ApiToken);
        Assert.Null(row.FromNumber);
        Assert.Null(row.TemplateId);
        Assert.Empty(harness.Protector.ProtectedPlaintexts);
    }

    // Decisión 28: fuera de Own los campos propios no se guardan, no se cifran y no se auditan.
    [Fact]
    public async Task DisabledWithOwnFieldsAuditsOnlyTheModeChangeAndNeverEncrypts()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(
            Command("Disabled", 1, "Zenvia", Key, FromNumber, TemplateId), TestContext.Current.CancellationToken);

        Assert.Empty(harness.Protector.ProtectedPlaintexts);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.ModeChangedAction}, Actions(harness));
    }

    [Fact]
    public async Task SharedWithOwnFieldsIgnoresThemAndTouchesNothing()
    {
        var harness = NewHandler();

        var dto = await harness.Handler.HandleAsync(
            Command("Shared", 1, "Zenvia", Key, FromNumber, TemplateId), TestContext.Current.CancellationToken);

        Assert.Equal("Shared", dto.Mode);
        Assert.Empty(harness.Repository.Rows);
        Assert.Empty(harness.Protector.ProtectedPlaintexts);
        Assert.Equal(0, harness.UnitOfWork.Saves);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task AFirstOwnSaveCreatesTheRowAtVersionTwoAndAuditsEachClassOfChange()
    {
        var harness = NewHandler();

        var dto = await harness.Handler.HandleAsync(FullOwn(), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.True(dto.ApiKeyConfigured);
        Assert.True(dto.ApiKeyReadable);
        Assert.Equal(Now, dto.ApiKeyUpdatedAt);
        Assert.Equal(1, harness.UnitOfWork.Saves);
        Assert.Equal(
            new[] {
                UpdateWhatsAppSettingsHandler.ModeChangedAction,
                UpdateWhatsAppSettingsHandler.ApiKeyReplacedAction,
                UpdateWhatsAppSettingsHandler.UpdatedAction,
            },
            Actions(harness));
        Assert.All(harness.Audit.Entries, entry =>
        {
            Assert.Equal(TenantId, entry.TenantId);
            Assert.Equal(SubjectId, entry.ActorId);
            Assert.Equal(TenantId.ToString(), entry.ResourceId);
            Assert.Equal("success", entry.Outcome);
        });
    }

    // Review Focus 1: lo que se pega de la consola de Zenvia trae casi siempre un salto de línea.
    [Fact]
    public async Task AnApiKeyWithSurroundingWhitespaceIsProtectedTrimmed()
    {
        var harness = NewHandler();

        await harness.Handler.HandleAsync(FullOwn(apiKey: "  zenvia-token-123\r\n"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] {Key}, harness.Protector.ProtectedPlaintexts);
    }

    [Fact]
    public async Task WithoutApiKeyTheStoredSecretIsKept()
    {
        var harness = NewHandler();
        var token = harness.Protector.Seed("k2", Key);
        harness.Repository.Add(OwnRow(token));

        await harness.Handler.HandleAsync(
            Command("Own", 2, templateId: "11111111-2222-3333-4444-555555555555"),
            TestContext.Current.CancellationToken);

        var row = Assert.Single(harness.Repository.Rows);
        Assert.Same(token, row.ApiToken);
        Assert.Same(token.Ciphertext, row.ApiToken!.Ciphertext);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.UpdatedAction}, Actions(harness));
    }

    [Fact]
    public async Task ReplacingTheKeyAloneAuditsOnlyTheReplacement()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k2", Key)));

        await harness.Handler.HandleAsync(Command("Own", 2, apiKey: Key), TestContext.Current.CancellationToken);

        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.ApiKeyReplacedAction}, Actions(harness));
    }

    [Fact]
    public async Task AKeyInANonActiveKeyIsReencryptedWithTheActiveOne()
    {
        var harness = NewHandler();
        var row = OwnRow(harness.Protector.Seed("k1", Key));
        harness.Repository.Add(row);
        var updatedAt = row.ApiTokenUpdatedAt;

        await harness.Handler.HandleAsync(Command("Own", 2), TestContext.Current.CancellationToken);

        Assert.Equal("k2", row.ApiToken!.KeyId);
        Assert.Equal(new[] {Key}, harness.Protector.ProtectedPlaintexts);
        Assert.Equal(updatedAt, row.ApiTokenUpdatedAt);
        Assert.Equal(new[] {UpdateWhatsAppSettingsHandler.UpdatedAction}, Actions(harness));
    }

    // Decisión 25: el drenaje nunca convierte un guardado en un 500.
    [Fact]
    public async Task DisabledOverANonActiveKeyThatCannotBeDecryptedSavesTheModeWithoutReencrypting()
    {
        var harness = NewHandler();
        var garbage = FakeWhatsAppSecretProtector.Garbage("k1");
        var row = OwnRow(garbage);
        harness.Repository.Add(row);

        var dto = await harness.Handler.HandleAsync(Command("Disabled", 2), TestContext.Current.CancellationToken);

        Assert.Equal(WhatsAppMode.Disabled, row.Mode);
        Assert.Same(garbage, row.ApiToken);
        Assert.Empty(harness.Protector.ProtectedPlaintexts);
        Assert.False(dto.ApiKeyReadable);
        Assert.Equal(1, harness.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AModeChangeAndADrainBumpTheVersionOnceAndPublishTwoActions()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k1", Key)));

        var dto = await harness.Handler.HandleAsync(Command("Disabled", 2), TestContext.Current.CancellationToken);

        Assert.Equal(3, dto.Version);
        Assert.Equal(
            new[] {UpdateWhatsAppSettingsHandler.ModeChangedAction, UpdateWhatsAppSettingsHandler.UpdatedAction},
            Actions(harness));
    }

    [Fact]
    public async Task ANoOpSavesNothingAndAuditsNothing()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k2", Key)));

        var dto = await harness.Handler.HandleAsync(
            Command("Own", 2, "Zenvia", null, FromNumber, TemplateId), TestContext.Current.CancellationToken);

        Assert.Equal(2, dto.Version);
        Assert.Equal(0, harness.UnitOfWork.Saves);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task SharedWithoutARowIsANoOpThatAddsNoRow()
    {
        var harness = NewHandler();

        var dto = await harness.Handler.HandleAsync(Command("Shared"), TestContext.Current.CancellationToken);

        Assert.Equal("Shared", dto.Mode);
        Assert.Equal(1, dto.Version);
        Assert.Empty(harness.Repository.Rows);
        Assert.Equal(0, harness.UnitOfWork.Saves);
        Assert.Empty(harness.Audit.Entries);
    }

    [Fact]
    public async Task WithoutARowAnyVersionOtherThanOneIsAConflict()
    {
        var harness = NewHandler();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            harness.Handler.HandleAsync(Command("Disabled", 2), TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task AStaleVersionIsAConflict()
    {
        var harness = NewHandler();
        harness.Repository.Add(OwnRow(harness.Protector.Seed("k2", Key)));

        await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            harness.Handler.HandleAsync(Command("Disabled", 1), TestContext.Current.CancellationToken));

        Assert.Equal(0, harness.UnitOfWork.Saves);
    }
}
