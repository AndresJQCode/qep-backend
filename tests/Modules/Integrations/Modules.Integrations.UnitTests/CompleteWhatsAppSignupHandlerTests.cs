using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>Spec 2026-10-09 §8.1: el orden de los chequeos, el camino FINISH, la coexistencia con uno y
/// con varios números, y cada error de Graph con su código. El PIN llega del formulario o se genera, y en
/// los dos casos queda cifrado en la conexión: sin él no hay re-registro posible.</summary>
public sealed class CompleteWhatsAppSignupHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CompleteWhatsAppSignupCommand Finish(Guid tenantId, string name = "Ventas", string? phoneNumberId = "111", string? pin = null) =>
        new(tenantId, name, "new_number", "FINISH", "AQB-code", "222", phoneNumberId, "333", pin);

    private static CompleteWhatsAppSignupCommand Coexistence(Guid tenantId, string? pin = null) =>
        new(tenantId, "Ventas", "existing_business_app", "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING", "AQB-code", "222", null, null, pin);

    [Fact]
    public async Task FinishExchangesRegistersSubscribesReadsAndCreatesTheConnectionWithItsRoute()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", "+57 300 123 4567", "Origen", "GREEN");

        var response = await bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct);

        Assert.Equal("whatsapp-cloud", response.ProviderKey);
        Assert.Equal("Active", response.Status);
        Assert.Equal("+57 300 123 4567", response.Fields["displayPhoneNumber"]);
        Assert.Equal("111", response.Fields["phoneNumberId"]);
        Assert.Equal("222", response.Fields["wabaId"]);
        Assert.True(response.Secrets["accessToken"].Configured);
        Assert.Equal(["exchange", "register:111", "subscribe:222", "read:111"], bed.Gateway.Calls);
        Assert.Matches("^[0-9]{6}$", bed.Gateway.LastPin!);
        Assert.True(response.Secrets["registrationPin"].Configured);
        var route = Assert.Single(bed.Routes.Added);
        Assert.Equal(("whatsapp-cloud", "111", "222", bed.TenantId, response.Id), (route.ProviderKey, route.ExternalId, route.AccountId, route.TenantId, route.ConnectionId));
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal(ConnectionAuditActions.Created, audit.Action);
        Assert.Contains("path:new_number", audit.ChangedFields);
        Assert.Contains("event:FINISH", audit.ChangedFields);
        Assert.DoesNotContain(audit.ChangedFields, field => field.Contains("AQB-code", StringComparison.Ordinal));
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // El PIN es del negocio: si el número ya tiene verificación en dos pasos, Meta exige el vigente.
    [Fact]
    public async Task AReceivedPinIsTheOneSentToMetaAndTheOneStored()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", null, null, null);

        var response = await bed.SignupHandler().HandleAsync(Finish(bed.TenantId, pin: "482913"), Ct);

        Assert.Equal("482913", bed.Gateway.LastPin);
        Assert.True(response.Secrets["registrationPin"].Configured);
        Assert.True(bed.Protector.TryUnprotect(response.Id, "registrationPin", bed.Repository.Connections.Single().Secrets.Single(secret => secret.FieldKey == "registrationPin").Protected, out var stored));
        Assert.Equal("482913", stored);
    }

    [Fact]
    public async Task CoexistenceSkipsRegistrationAndPicksTheOnlyNumber()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.WabaNumbers["222"] = [new WabaPhoneNumber("111", "+57 1", "Origen", "GREEN")];
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", "+57 1", "Origen", "GREEN");

        var response = await bed.SignupHandler().HandleAsync(Coexistence(bed.TenantId, pin: "482913"), Ct);

        Assert.Equal("111", response.Fields["phoneNumberId"]);
        Assert.Equal(["exchange", "numbers:222", "subscribe:222", "read:111"], bed.Gateway.Calls);
        // Sin register no hay PIN que guardar, aunque el formulario haya mandado uno.
        Assert.False(response.Secrets["registrationPin"].Configured);
    }

    // D-M15: con varios sin ruta no se elige ninguno; el que ya tiene ruta se descarta.
    [Fact]
    public async Task CoexistenceWithSeveralFreeNumbersIsRegistrationFailed()
    {
        var bed = new IntegrationsTestBed();
        bed.Routes.Existing.Add(("whatsapp-cloud", "999"));
        bed.Gateway.WabaNumbers["222"] = [new WabaPhoneNumber("111", null, null, null), new WabaPhoneNumber("112", null, null, null), new WabaPhoneNumber("999", null, null, null)];

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Coexistence(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppRegistrationFailed, error.Code);
        Assert.Contains("varios números", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Theory]
    [InlineData("exchange", IntegrationsErrorCodes.WhatsAppCodeExchangeFailed)]
    [InlineData("register", IntegrationsErrorCodes.WhatsAppRegistrationFailed)]
    [InlineData("subscribe", IntegrationsErrorCodes.WhatsAppRegistrationFailed)]
    [InlineData("read", IntegrationsErrorCodes.WhatsAppRegistrationFailed)]
    public async Task EveryGraphFailureHasItsCodeAndSavesNothing(string failingStep, string expectedCode)
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.PhoneNumbers["111"] = new WabaPhoneNumber("111", null, null, null);
        bed.Gateway.FailAt = failingStep;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Routes.Added);
    }

    // Graph 133005: el PIN no es el del número. Viaja como error del campo para que el formulario lo marque.
    [Fact]
    public async Task APinMismatchNamesThePinField()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.FailAt = "register";
        bed.Gateway.FailureCode = 133005;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId, pin: "482913"), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppRegistrationFailed, error.Code);
        Assert.Contains("WhatsApp Manager", Assert.Single(error.FieldErrors["pin"]), StringComparison.Ordinal);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    // Graph 133016: diez registros en 72 h. Reintentar empeora, así que tiene código propio.
    [Fact]
    public async Task TheRegistrationLimitHasItsOwnCode()
    {
        var bed = new IntegrationsTestBed();
        bed.Gateway.FailAt = "register";
        bed.Gateway.FailureCode = 133016;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppRegistrationLimit, error.Code);
        Assert.Empty(error.FieldErrors);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    // Antes del canje sólo hay validaciones de milisegundos: tope, nombre y ruta.
    [Fact]
    public async Task AnExistingRouteFailsBeforeTalkingToMeta()
    {
        var bed = new IntegrationsTestBed();
        bed.Routes.Existing.Add(("whatsapp-cloud", "111"));

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task WithoutMetaAppTheExchangeFailsBeforeTalkingToMeta()
    {
        var bed = new IntegrationsTestBed();
        bed.MetaApp.Configured = false;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() => bed.SignupHandler().HandleAsync(Finish(bed.TenantId), Ct));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppCodeExchangeFailed, error.Code);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Theory]
    [InlineData("event", "FINISH_OTHER", "event")]
    [InlineData("path", "something", "path")]
    [InlineData("phoneNumberId", null, "phoneNumberId")]
    [InlineData("phoneNumberId", "abc", "phoneNumberId")]
    [InlineData("wabaId", "", "wabaId")]
    [InlineData("code", "", "code")]
    [InlineData("name", "", "name")]
    [InlineData("pin", "12345", "pin")]
    [InlineData("pin", "abcdef", "pin")]
    [InlineData("pin", "", "pin")]
    public async Task TheValidatorNamesTheField(string property, string? value, string expectedKey)
    {
        var bed = new IntegrationsTestBed();
        var command = Finish(bed.TenantId);
        command = property switch
        {
            "event" => command with { Event = value },
            "path" => command with { Path = value },
            "phoneNumberId" => command with { PhoneNumberId = value },
            "wabaId" => command with { WabaId = value },
            "code" => command with { Code = value },
            "pin" => command with { Pin = value },
            _ => command with { Name = value },
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.SignupHandler().HandleAsync(command, Ct));

        Assert.Contains(error.Errors, failure => failure.PropertyName == expectedKey);
        Assert.Empty(bed.Gateway.Calls);
    }

    [Fact]
    public async Task AnotherTenantOrAMissingPermissionIsForbiddenBeforeAnything()
    {
        var bed = new IntegrationsTestBed();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SignupHandler(new FakeExecutionContext(Guid.CreateVersion7(), bed.SubjectId, IntegrationsPermissions.ConnectionManage))
                .HandleAsync(Finish(bed.TenantId), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.SignupHandler(new FakeExecutionContext(bed.TenantId, bed.SubjectId, IntegrationsPermissions.ConnectionRead))
                .HandleAsync(Finish(bed.TenantId), Ct));
        Assert.Empty(bed.Gateway.Calls);
    }
}
