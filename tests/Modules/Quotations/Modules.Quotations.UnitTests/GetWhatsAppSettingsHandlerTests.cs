using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>Spec 2026-10-07: el GET nunca crea fila, dice si la key se puede leer descifrándola de
/// verdad (TryUnprotect, valor descartado) y no expone el token ni por nombre de propiedad.</summary>
public sealed class GetWhatsAppSettingsHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedModes = ["Shared", "Own", "Disabled"];
    private static readonly string[] ExpectedProviders = ["Zenvia"];
    private static readonly string[] ExpectedPropertyNames =
    [
        "ApiKeyConfigured", "ApiKeyReadable", "ApiKeyUpdatedAt", "FromNumber", "Mode", "Modes",
        "Provider", "Providers", "TemplateId", "TenantId", "Version",
    ];

    private static (GetWhatsAppSettingsHandler Handler, InMemoryTenantWhatsAppSettingsRepository Repository, FakeWhatsAppSecretProtector Protector)
        NewHandler(IExecutionContext? executionContext = null, FixedTenantModules? modules = null)
    {
        var repository = new InMemoryTenantWhatsAppSettingsRepository();
        var protector = new FakeWhatsAppSecretProtector();
        var handler = new GetWhatsAppSettingsHandler(
            repository,
            protector,
            modules ?? FixedTenantModules.AllEnabled(),
            executionContext ?? new StubExecutionContext(SubjectId, TenantId),
            new FixedClock(Now));
        return (handler, repository, protector);
    }

    private static TenantWhatsAppSettings OwnRow(ProtectedSecret token, WhatsAppMode finalMode = WhatsAppMode.Own)
    {
        var row = TenantWhatsAppSettings.CreateEmpty(TenantId, Now);
        row.Configure(WhatsAppMode.Own, WhatsAppProvider.Zenvia, token, null, "573001234567",
            "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f", Now);
        if (finalMode != WhatsAppMode.Own)
        {
            row.Configure(finalMode, null, null, null, null, null, Now);
        }

        return row;
    }

    [Fact]
    public async Task WithoutARowItIsSharedAtVersionOneWithTheFullCollections()
    {
        var (handler, repository, _) = NewHandler();

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(TenantId, dto.TenantId);
        Assert.Equal("Shared", dto.Mode);
        Assert.Null(dto.Provider);
        Assert.False(dto.ApiKeyConfigured);
        Assert.Null(dto.ApiKeyUpdatedAt);
        Assert.Null(dto.ApiKeyReadable);
        Assert.Null(dto.FromNumber);
        Assert.Null(dto.TemplateId);
        Assert.Equal(1, dto.Version);
        Assert.Equal(ExpectedModes, dto.Modes);
        Assert.Equal(ExpectedProviders, dto.Providers);
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public async Task AReadableKeyIsReportedReadable()
    {
        var (handler, repository, protector) = NewHandler();
        repository.Add(OwnRow(protector.Seed("k2", "token")));

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.True(dto.ApiKeyConfigured);
        Assert.True(dto.ApiKeyReadable);
        Assert.Equal(Now, dto.ApiKeyUpdatedAt);
    }

    [Fact]
    public async Task AKeyStoredWithAKeyIdThatIsNotConfiguredIsNotReadable()
    {
        var (handler, repository, protector) = NewHandler();
        repository.Add(OwnRow(protector.Seed("k1", "token")));
        protector.KnownKeys.Remove("k1");

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.True(dto.ApiKeyConfigured);
        Assert.False(dto.ApiKeyReadable);
    }

    [Fact]
    public async Task AlteredBytesWithTheKeyConfiguredAreNotReadable()
    {
        var (handler, repository, _) = NewHandler();
        repository.Add(OwnRow(FakeWhatsAppSecretProtector.Garbage("k2")));

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.False(dto.ApiKeyReadable);
    }

    // Lo que la pantalla muestra al volver a "Cuenta propia".
    [Theory]
    [InlineData(WhatsAppMode.Shared)]
    [InlineData(WhatsAppMode.Disabled)]
    public async Task SharedAndDisabledStillReturnTheStoredOwnAccount(WhatsAppMode mode)
    {
        var (handler, repository, protector) = NewHandler();
        repository.Add(OwnRow(protector.Seed("k2", "token"), mode));

        var dto = await handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken);

        Assert.Equal(mode.ToString(), dto.Mode);
        Assert.Equal("Zenvia", dto.Provider);
        Assert.Equal("573001234567", dto.FromNumber);
        Assert.True(dto.ApiKeyReadable);
    }

    [Fact]
    public async Task WithoutTheQuotationsModuleIsModuleNotEnabled()
    {
        var modules = FixedTenantModules.Without(TenantModuleKeys.Quotations);
        var (handler, repository, _) = NewHandler(modules: modules);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, repository.FindCalls);
        Assert.Equal(1, modules.FindCalls);
    }

    [Fact]
    public async Task WithoutSettingsReadIsForbidden()
    {
        var (handler, _, _) = NewHandler(new StubExecutionContext(SubjectId, TenantId, TenancyPermissions.SettingsRead));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new GetWhatsAppSettingsQuery(TenantId), TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    // Criterio 5: ninguna propiedad del DTO es el token, ni lo será sin que esta lista cambie.
    [Fact]
    public void NoPropertyOfTheDtoCarriesTheKey()
    {
        var names = typeof(WhatsAppSettingsDto).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal);

        Assert.Equal(ExpectedPropertyNames, names);
    }
}
