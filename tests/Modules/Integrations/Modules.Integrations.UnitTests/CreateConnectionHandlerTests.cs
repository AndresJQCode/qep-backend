using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, <c>POST /connections</c> y decisión 5: valida, prueba y recién ahí guarda. Una
/// credencial rechazada o un proveedor inalcanzable no dejan nada guardado.
/// </summary>
public sealed class CreateConnectionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateConnectionCommand Command(IntegrationsTestBed bed, string name = "WhatsApp sede norte") =>
        new(
            bed.TenantId,
            "zenvia",
            name,
            new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = IntegrationsTestBed.FromNumber },
            new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = IntegrationsTestBed.Token });

    [Fact]
    public async Task CreatesAnActiveConnectionAfterAPassingTestAndAuditsKeysNotValues()
    {
        var bed = new IntegrationsTestBed();

        var response = await bed.CreateHandler().HandleAsync(Command(bed), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Equal(1, response.Version);
        Assert.Equal(IntegrationsTestBed.Now, response.LastVerifiedAt);
        Assert.Equal(bed.MemberId, response.CreatedBy.MemberId);
        Assert.Equal(IntegrationsTestBed.AuthorName, response.CreatedBy.DisplayName);
        Assert.True(response.Secrets[ZenviaFieldKeys.ApiToken].Readable);
        var call = Assert.Single(bed.Tester.Calls);
        Assert.Equal("zenvia", call.ProviderKey);
        Assert.Equal(IntegrationsTestBed.Token, call.Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.Equal(IntegrationsTestBed.FromNumber, call.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Single(bed.Repository.Connections);
        Assert.Equal(1, bed.UnitOfWork.Saves);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.created", audit.Action);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(AuditActorType.Human, audit.ActorType);
        Assert.Equal(bed.SubjectId, audit.ActorId);
        Assert.Equal(["apiToken", "fromNumber", "name"], audit.ChangedFields);
    }

    [Fact]
    public async Task RejectedCredentialsSaveNothingAndMarkTheSecretField()
    {
        var bed = new IntegrationsTestBed();
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("integrations.connection.credentials_rejected", error.Code);
        Assert.Equal(["secrets.apiToken"], error.FieldErrors.Keys);
        Assert.Empty(bed.Repository.Connections);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
    }

    // D3: «no pude verificar» también bloquea el guardado.
    [Fact]
    public async Task AnUnreachableProviderBlocksTheSave()
    {
        var bed = new IntegrationsTestBed();
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("integrations.connection.provider_unreachable", error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Empty(bed.Repository.Connections);
    }

    // P12: si el proveedor dice que un campo no sirve, el 422 marca ese campo.
    [Fact]
    public async Task AFieldTheProviderRejectsGoesToThatField()
    {
        var bed = new IntegrationsTestBed();
        bed.Tester.Result = ConnectionTestResult.Invalid(ZenviaFieldKeys.FromNumber, "unknown sender");

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("fields.fromNumber", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Repository.Connections);
    }

    // P18: sin llave activa nada se puede guardar, así que el 503 sale antes de validar o probar.
    [Fact]
    public async Task WithoutAnActiveKeyItIs503BeforeValidatingOrCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        bed.Protector.ActiveKeyId = null;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            bed.CreateHandler().HandleAsync(Command(bed, name: ""), Ct));

        Assert.Equal("integrations.secret_protection.unavailable", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task WithoutManageOrForAnotherTenantItIsForbiddenBeforeAnything()
    {
        var bed = new IntegrationsTestBed();
        var otherTenant = bed.Context(Guid.CreateVersion7());
        bed.Permissions = [IntegrationsPermissions.ConnectionRead];

        foreach (var context in new[] { bed.Context(), otherTenant })
        {
            var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
                bed.CreateHandler(context).HandleAsync(Command(bed, name: ""), Ct));
            Assert.Equal("authorization.denied", error.Code);
        }

        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task AHiddenProviderIsModuleNotEnabled()
    {
        var bed = new IntegrationsTestBed();
        bed.HideQuotations();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    // D1: el tope se mira antes de llamar al proveedor.
    [Fact]
    public async Task TheTwentyFirstConnectionIsLimitReachedWithoutCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        for (var index = 0; index < 20; index++)
        {
            bed.Seed($"Línea {index}");
        }

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("integrations.connection.limit_reached", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task ASubjectWithoutAnActiveMembershipIsForbidden()
    {
        var bed = new IntegrationsTestBed();
        bed.Memberships.Active.Clear();

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(Command(bed), Ct));

        Assert.Equal("authorization.denied", error.Code);
    }

    // Un diccionario null (no un valor null) es vacío: 422 por campo requerido, nunca un 500.
    [Fact]
    public async Task NullFieldsAndSecretsDictionariesAreValidationErrorsNotServerErrors()
    {
        var bed = new IntegrationsTestBed();

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.CreateHandler().HandleAsync(
            new CreateConnectionCommand(bed.TenantId, "zenvia", "WhatsApp sede norte", null, null), Ct));

        Assert.Equal(
            ["fields.fromNumber", "secrets.apiToken"],
            error.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal));
        Assert.Empty(bed.Tester.Calls);
        Assert.Empty(bed.Repository.Connections);
    }

    // Criterio 5 del spec: un proveedor nuevo es una entrada de catálogo y su probador; los handlers
    // no cambian. Éste sólo existe en esta prueba.
    [Fact]
    public async Task AProviderRegisteredOnlyInTestsWorksWithoutTouchingTheHandlers()
    {
        var fakeCrm = new IntegrationProvider(
            "fake-crm",
            "CRM de prueba",
            IntegrationCategory.Messaging,
            [TenantModuleKeys.Quotations],
            [
                new FieldDefinition("endpoint", "URL", FieldKind.Url, required: true, maxLength: 200, pattern: "^https://", invalidMessage: "Usa una URL https."),
                new FieldDefinition("apiKey", "Clave", FieldKind.Secret, required: true, maxLength: 100, pattern: null, invalidMessage: "Revisa la clave."),
            ],
            maxConnections: 2);
        var bed = new IntegrationsTestBed(new FakeIntegrationProviderCatalog(IntegrationProviders.Zenvia, fakeCrm));

        var response = await bed.CreateHandler().HandleAsync(
            new CreateConnectionCommand(
                bed.TenantId,
                "fake-crm",
                "CRM",
                new Dictionary<string, string?> { ["endpoint"] = "https://crm.example.com" },
                new Dictionary<string, string?> { ["apiKey"] = "crm-key" }),
            Ct);

        Assert.Equal("fake-crm", response.ProviderKey);
        Assert.Equal(["endpoint"], response.Fields.Keys);
        Assert.Equal(["apiKey"], response.Secrets.Keys);
        Assert.Equal("fake-crm", Assert.Single(bed.Tester.Calls).ProviderKey);
    }
}
