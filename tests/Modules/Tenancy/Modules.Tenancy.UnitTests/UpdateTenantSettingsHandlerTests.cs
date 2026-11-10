using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

public sealed class UpdateTenantSettingsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly Tenant _tenant = Tenant.Create(
        TenantId.New(), "qcode-demo", "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd",
        MembershipId.New(), Now);

    [Fact]
    public async Task HandlePassesCurrencyAndNumberFormatToTheAggregate()
    {
        var result = await Handler().HandleAsync(
            Command(defaultCurrency: "usd", numberFormat: "1,234.56"),
            TestContext.Current.CancellationToken);

        Assert.Equal("USD", result.DefaultCurrency);
        Assert.Equal("1,234.56", result.NumberFormat);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task HandleRejectsAnEmptyCurrencyThroughTheValidator()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Handler().HandleAsync(Command(defaultCurrency: ""), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandlePassesTheCountryToTheAggregate()
    {
        var result = await Handler().HandleAsync(
            Command(country: "mx"),
            TestContext.Current.CancellationToken);

        Assert.Equal("MX", result.Country);
        Assert.Equal(2, result.Version);
    }

    // Ventana de despliegue: un frontend anterior al país no lo manda. Sin él se conserva el
    // guardado, y como nada más cambió, la versión no sube.
    [Fact]
    public async Task HandleKeepsTheStoredCountryWhenTheCommandOmitsIt()
    {
        var result = await Handler().HandleAsync(
            Command(country: null),
            TestContext.Current.CancellationToken);

        Assert.Equal("CO", result.Country);
        Assert.Equal(1, result.Version);
    }

    // Ventana de despliegue: un frontend anterior al requisito no lo manda. Sin él se conserva lo
    // guardado, y como nada más cambió, la versión no sube.
    [Fact]
    public async Task ANullPersonTypeRequirementKeepsTheStoredOne()
    {
        await Handler().HandleAsync(
            Command(personTypeRequirement: "OnCustomerSave"), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(
            Command(personTypeRequirement: null, expectedVersion: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal("OnCustomerSave", result.PersonTypeRequirement);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task AnUnknownPersonTypeRequirementIsRejected()
    {
        var exception = await Assert.ThrowsAsync<TenantDomainException>(() =>
            Handler().HandleAsync(
                Command(personTypeRequirement: "Always"), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.settings.person_type_requirement.invalid", exception.Code);
    }

    // El valor se guarda aunque el tenant no sea colombiano: sólo deja de aplicar (Effective).
    [Fact]
    public async Task ANonColombianTenantStillStoresTheValue()
    {
        var result = await Handler().HandleAsync(
            Command(country: "ES", personTypeRequirement: "OnOrderApproval"),
            TestContext.Current.CancellationToken);

        Assert.Equal("ES", result.Country);
        Assert.Equal("OnOrderApproval", result.PersonTypeRequirement);
    }

    private static readonly CustomerFieldPolicyInput RelaxedInput = new(
        new FieldRequirementInput(false, true), new FieldRequirementInput(false, false));

    [Fact]
    public async Task APresentPolicyIsSavedAndReturned()
    {
        var result = await Handler().HandleAsync(
            Command(customerFieldPolicy: RelaxedInput), TestContext.Current.CancellationToken);

        Assert.Equal(RelaxedInput.ToDomain(), result.CustomerFieldPolicy);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task AnAbsentPolicyKeepsTheStoredOne()
    {
        await Handler().HandleAsync(
            Command(customerFieldPolicy: RelaxedInput), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(
            Command(customerFieldPolicy: null, expectedVersion: 2), TestContext.Current.CancellationToken);

        Assert.Equal(RelaxedInput.ToDomain(), result.CustomerFieldPolicy);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task APolicyWithAMissingFlagIsAValidationFailure()
    {
        var policy = new CustomerFieldPolicyInput(
            new FieldRequirementInput(true, true), new FieldRequirementInput(true, null));

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Handler().HandleAsync(Command(customerFieldPolicy: policy), TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, error => error.PropertyName == "CustomerFieldPolicy.City.OnBilling");
    }

    [Fact]
    public async Task APolicyWithANullDocumentIsAValidationFailure()
    {
        var policy = new CustomerFieldPolicyInput(null, new FieldRequirementInput(true, true));

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Handler().HandleAsync(Command(customerFieldPolicy: policy), TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, error => error.PropertyName == "CustomerFieldPolicy.Document");
    }

    [Fact]
    public async Task APolicyWithANullCityIsAValidationFailure()
    {
        var policy = new CustomerFieldPolicyInput(new FieldRequirementInput(true, true), null);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            Handler().HandleAsync(Command(customerFieldPolicy: policy), TestContext.Current.CancellationToken));

        Assert.Contains(exception.Errors, error => error.PropertyName == "CustomerFieldPolicy.City");
    }

    [Fact]
    public async Task AnInconsistentPolicyIsTheDomainCode()
    {
        var policy = new CustomerFieldPolicyInput(
            new FieldRequirementInput(true, true), new FieldRequirementInput(true, false));

        var exception = await Assert.ThrowsAsync<TenantDomainException>(() =>
            Handler().HandleAsync(Command(customerFieldPolicy: policy), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.settings.customer_field_policy.invalid", exception.Code);
    }

    private UpdateTenantSettingsCommand Command(
        string defaultCurrency = "COP", string numberFormat = "1.234,56", string? country = "CO",
        string? personTypeRequirement = null, long expectedVersion = 1,
        CustomerFieldPolicyInput? customerFieldPolicy = null) =>
        new(_tenant.Id, "QCode Demo", "es-CO", "America/Bogota", "yyyy-MM-dd",
            defaultCurrency, numberFormat, country, expectedVersion, "corr-1", personTypeRequirement,
            customerFieldPolicy);

    private UpdateTenantSettingsHandler Handler() =>
        new(
            new InMemoryTenantRepository(_tenant),
            new RecordingTenancyUnitOfWork([]),
            new SettingsUpdateExecutionContext(_tenant.Id),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            new UpdateTenantSettingsValidator(),
            new RecordingTenantLogoStorage([]));
}
