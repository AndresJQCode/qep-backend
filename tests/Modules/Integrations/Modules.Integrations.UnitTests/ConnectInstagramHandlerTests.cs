using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-14 §8.1: el orden de los chequeos, el camino feliz (code → token corto → token largo →
/// /me → suscripción → conexión con su ruta) y cada rechazo con su código. El token nunca aparece en la auditoría.</summary>
public sealed class ConnectInstagramHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string RedirectUri = "https://qep.qcode.co/settings/integrations/instagram/callback";

    private static ConnectInstagramCommand Connect(Guid tenantId, string name = "Instagram Ventas", string? code = "AQB-ig-code", string redirectUri = RedirectUri) =>
        new(tenantId, name, code, redirectUri);

    [Fact]
    public async Task ExchangesTheCodeReadsTheAccountSubscribesAndCreatesTheConnectionWithItsRoute()
    {
        var bed = new IntegrationsTestBed();

        var response = await bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct);

        Assert.Equal("instagram", response.ProviderKey);
        Assert.Equal("Active", response.Status);
        Assert.Equal("origenbotanico", response.Fields["username"]);
        Assert.Equal("Origen Botánico", response.Fields["accountName"]);
        Assert.Equal(FakeInstagramLoginGateway.IgUserId, response.Fields["igUserId"]);
        // 60 días desde el reloj de la prueba, en ISO 8601 UTC: lo lee el worker de refresco.
        Assert.Equal(IntegrationsTestBed.Now.AddSeconds(FakeInstagramLoginGateway.ExpiresInSeconds).ToString("O"), response.Fields["tokenExpiresAt"]);
        Assert.True(response.Secrets["accessToken"].Configured);
        Assert.True(response.Secrets["accessToken"].Readable);
        Assert.Equal(["exchange", "long-lived", "me", $"subscribe:{FakeInstagramLoginGateway.IgUserId}"], bed.Gateway.Instagram.Calls);
        Assert.Equal(RedirectUri, bed.Gateway.Instagram.LastRedirectUri);
        var route = Assert.Single(bed.Routes.Added);
        Assert.Equal(("instagram", FakeInstagramLoginGateway.IgUserId, null, bed.TenantId, response.Id), (route.ProviderKey, route.ExternalId, route.AccountId, route.TenantId, route.ConnectionId));
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(ConnectionAuditActions.Created, audit.Action);
        Assert.Contains("onboarding:instagram_login", audit.ChangedFields);
        Assert.DoesNotContain(audit.ChangedFields, field => field.Contains("AQB-ig-code", StringComparison.Ordinal) || field.Contains("SENTINEL", StringComparison.Ordinal));
        Assert.Equal(1, bed.UnitOfWork.Saves);
        // El token largo es el que se guarda, nunca el corto.
        Assert.True(bed.Protector.TryUnprotect(response.Id, "accessToken", bed.Repository.Connections.Single().Secrets.Single().Protected, out var stored));
        Assert.Equal(FakeInstagramLoginGateway.LongLivedToken, stored);
    }

    [Fact]
    public async Task ARejectedCodeIsCodeExchangeFailedAndNothingIsSaved()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.Instagram.FailAt = "exchange";

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramCodeExchangeFailed, error.Code);
        Assert.Empty(bed.Repository.Connections);
        Assert.Empty(bed.Routes.Added);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task AFailedLongLivedExchangeIsCodeExchangeFailed()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.Instagram.FailAt = "long-lived";

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramCodeExchangeFailed, error.Code);
        Assert.Equal(["exchange", "long-lived"], bed.Gateway.Instagram.Calls);
    }

    // La persona desmarcó un permiso en la ventana de Meta: sin los dos no se puede leer ni responder.
    [Theory]
    [InlineData("instagram_business_basic")]
    [InlineData("instagram_business_manage_messages")]
    [InlineData("")]
    public async Task MissingPermissionsArePermissionsMissingBeforeAnythingElseIsCalled(string granted)
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.Instagram.Permissions = granted.Length == 0 ? [] : [granted];

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramPermissionsMissing, error.Code);
        Assert.Equal(["exchange"], bed.Gateway.Instagram.Calls);
    }

    [Theory]
    [InlineData("PERSONAL")]
    [InlineData(null)]
    public async Task ANonProfessionalAccountIsAccountNotProfessional(string? accountType)
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.Instagram.AccountType = accountType;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramAccountNotProfessional, error.Code);
        Assert.DoesNotContain(bed.Gateway.Instagram.Calls, call => call.StartsWith("subscribe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACreatorAccountIsProfessional()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.Instagram.AccountType = "MEDIA_CREATOR";

        var response = await bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct);

        Assert.Equal("Active", response.Status);
    }

    [Fact]
    public async Task AnAccountRoutedToAnyTenantIsAccountAlreadyConnected()
    {
        var bed = new IntegrationsTestBed();
        bed.Routes.Existing.Add(("instagram", FakeInstagramLoginGateway.IgUserId));

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramAccountAlreadyConnected, error.Code);
        Assert.Empty(bed.Repository.Connections);
    }

    [Fact]
    public async Task AFailedSubscriptionIsSubscriptionFailed()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.Instagram.FailAt = "subscribe";

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramSubscriptionFailed, error.Code);
        Assert.Empty(bed.Repository.Connections);
    }

    [Fact]
    public async Task ARedirectUriOutsideTheAllowedOriginsIsAValidationFailureOnThatField()
    {
        var bed = new IntegrationsTestBed();
        bed.InstagramApp.AllowedOrigins.Clear();
        bed.InstagramApp.AllowedOrigins.Add("https://qep.qcode.co");

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.InstagramHandler().HandleAsync(Connect(bed.TenantId, redirectUri: "https://evil.example/callback"), Ct));

        Assert.Equal("redirectUri", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Gateway.Instagram.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://qep.qcode.co/x")]
    public async Task AMalformedRedirectUriIsAValidationFailure(string? redirectUri)
    {
        var bed = new IntegrationsTestBed();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.InstagramHandler().HandleAsync(new ConnectInstagramCommand(bed.TenantId, "Ventas", "AQB", redirectUri), Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "redirectUri");
    }

    [Fact]
    public async Task WithoutTheInstagramAppTheCodeCannotBeExchanged()
    {
        var bed = new IntegrationsTestBed();
        bed.InstagramApp.Configured = false;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.InstagramCodeExchangeFailed, error.Code);
        Assert.Empty(bed.Gateway.Instagram.Calls);
    }

    [Fact]
    public async Task TheLimitAndTheNameAreCheckedBeforeExchanging()
    {
        var bed = new IntegrationsTestBed();
        await bed.InstagramHandler().HandleAsync(Connect(bed.TenantId, "Ventas"), Ct);
        bed.Routes.Existing.Clear();
        bed.Gateway.Instagram.Calls.Clear();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId, "ventas"), Ct));

        Assert.Equal(IntegrationsErrorCodes.NameTaken, error.Code);
        Assert.Empty(bed.Gateway.Instagram.Calls);
    }

    [Fact]
    public async Task WithoutThePermissionItIsForbiddenBeforeValidating()
    {
        var bed = new IntegrationsTestBed { Permissions = [IntegrationsPermissions.ConnectionRead] };

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.InstagramHandler().HandleAsync(Connect(bed.TenantId), Ct));

        Assert.Empty(bed.Gateway.Instagram.Calls);
    }
}
