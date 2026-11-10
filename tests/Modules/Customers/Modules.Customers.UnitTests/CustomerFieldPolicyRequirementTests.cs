using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Tenancy.Domain;
using static Modules.Customers.UnitTests.CustomerDomainFixtures;

namespace Modules.Customers.UnitTests;

public sealed class CustomerFieldPolicyRequirementTests
{
    private const string NumberRequired = "customers.customer.identification_number_required";
    private static readonly Guid Tenant = CustomerWriteDoubles.Tenant;
    private static readonly CustomerFieldPolicy Relaxed = new(new(false, false), new(false, false));
    private static readonly CustomerFieldPolicy Strict = new(new(true, false), new(true, false));
    private static readonly CustomerFieldPolicy DocumentOnly = new(new(true, false), new(false, false));

    public static TheoryData<bool, bool, bool, string, string?> PolicyMatrix()
    {
        var data = new TheoryData<bool, bool, bool, string, string?>();
        foreach (var documentOnSave in new[] { false, true })
        {
            foreach (var cityOnSave in new[] { false, true })
            {
                foreach (var withData in new[] { false, true })
                {
                    foreach (var country in new[] { "CO", "ES" })
                    {
                        string? expected = null;
                        if (!withData)
                        {
                            expected = documentOnSave
                                ? CustomersErrorCodes.IdentificationRequired
                                : cityOnSave
                                    ? (country == "CO" ? CustomersErrorCodes.CityRequired : CustomersErrorCodes.CityNameRequired)
                                    : null;
                        }

                        data.Add(documentOnSave, cityOnSave, withData, country, expected);
                    }
                }
            }
        }

        return data;
    }

    private static UpdateCustomerCommand Command(
        string? type = "CC",
        string? number = "1020304050",
        string country = "CO",
        Guid? cityId = null,
        string? cityName = null) =>
        new(Tenant, Guid.NewGuid(), "Verde", null, type, number, "3109352187", null, null, country, cityId, cityName, null, false, false);

    private static Task Ensure(UpdateCustomerCommand command, CustomerFieldPolicy? policy, Customer? current = null) =>
        CustomerFieldPolicyRequirement.EnsureSatisfiedAsync(
            policy is null ? new CustomerWriteDoubles.TenantDirectory { Regional = null } : CustomerWriteDoubles.TenantDirectory.WithPolicy(policy),
            Tenant,
            command,
            current,
            CancellationToken.None);

    private static async Task<CustomersDomainException> Fails(Task task)
    {
        var failure = await Assert.ThrowsAsync<CustomersDomainException>(() => task);
        Assert.IsNotType<FluentValidation.ValidationException>(failure);
        return failure;
    }

    private static string[] Keys(CustomersDomainException failure) =>
        failure.FieldErrors.Keys.Order(StringComparer.Ordinal).ToArray();

    private static Customer CompleteWith(CustomerIdentification? identification, CustomerContactInfo contact) =>
        Customer.Create(
            CustomerId.New(), CustomerDomainFixtures.TenantId, "CLI08000142", "Verde Esencial", null, null,
            identification, null, contact, Commercial(), Now);

    private static CustomerContactInfo ColombianWithoutCity() =>
        new() { Phone = "310 935 2187", Address = "Calle 10 # 45-12", Country = "CO" };

    private static Customer Incomplete() =>
        Customer.CreateFromRegister(
            CustomerId.New(), CustomerDomainFixtures.TenantId, "Verde", null, "3109352187", Now, new FakeNormalizer());

    [Fact]
    public async Task ATypeWithoutNumberIsNumberRequiredOnTheNumberOnly()
    {
        foreach (var blank in new string?[] { null, "", "  " })
        {
            var failure = await Fails(Ensure(Command(number: blank), Relaxed));

            Assert.Equal(NumberRequired, failure.Code);
            Assert.Equal(["IdentificationNumber"], Keys(failure));
            Assert.Equal([NumberRequired], failure.FieldErrors["IdentificationNumber"]);
        }
    }

    [Fact]
    public async Task ANumberWithoutTypeIsIncompleteOnTheTypeOnly()
    {
        var failure = await Fails(Ensure(Command(type: null), Relaxed));

        Assert.Equal(CustomersErrorCodes.IdentificationIncomplete, failure.Code);
        Assert.Equal(["IdentificationType"], Keys(failure));
    }

    [Fact]
    public async Task TheShapeRuleRunsEvenWithARelaxedPolicy()
    {
        var failure = await Fails(Ensure(Command(number: null), Relaxed));

        Assert.Equal(NumberRequired, failure.Code);
    }

    [Fact]
    public async Task MissingDocumentAndCityReportsBothFields()
    {
        var co = await Fails(Ensure(Command(type: null, number: null), Strict));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, co.Code);
        Assert.Equal(["CityId", "IdentificationNumber", "IdentificationType"], Keys(co));
        Assert.Equal([CustomersErrorCodes.CityRequired], co.FieldErrors["CityId"]);
        Assert.Equal([CustomersErrorCodes.IdentificationRequired], co.FieldErrors["IdentificationType"]);
        Assert.Equal([CustomersErrorCodes.IdentificationRequired], co.FieldErrors["IdentificationNumber"]);

        var abroad = await Fails(Ensure(Command(type: null, number: null, country: "ES"), Strict));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, abroad.Code);
        Assert.Equal(["CityName", "IdentificationNumber", "IdentificationType"], Keys(abroad));
        Assert.Equal([CustomersErrorCodes.CityNameRequired], abroad.FieldErrors["CityName"]);
    }

    [Fact]
    public async Task WhitespaceOnlyFieldsAreNoDocument()
    {
        var command = Command(type: "  ", number: "  ", cityId: CityId);

        await Ensure(command, Relaxed);
        var failure = await Fails(Ensure(command, CustomerFieldPolicy.Default));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, failure.Code);
    }

    [Fact]
    public async Task NoDocumentWithOnCustomerSaveIsRequiredOnBothFields()
    {
        var failure = await Fails(Ensure(Command(type: null, number: null, cityId: CityId), DocumentOnly));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, failure.Code);
        Assert.Equal(["IdentificationNumber", "IdentificationType"], Keys(failure));
    }

    [Theory]
    [MemberData(nameof(PolicyMatrix))]
    public async Task TheSaveFlagsDecide(bool documentOnSave, bool cityOnSave, bool withData, string country, string? expectedCode)
    {
        var policy = new CustomerFieldPolicy(new(documentOnSave, false), new(cityOnSave, false));
        var command = withData
            ? Command(country: country, cityId: country == "CO" ? CityId : null, cityName: country == "CO" ? null : "Madrid")
            : Command(type: null, number: null, country: country);

        if (expectedCode is null)
        {
            await Ensure(command, policy);
            return;
        }

        var failure = await Fails(Ensure(command, policy));
        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public async Task WithoutRegionalSettingsTheDefaultRules()
    {
        var failure = await Fails(Ensure(Command(type: null, number: null, cityId: CityId), policy: null));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, failure.Code);
    }

    [Fact]
    public async Task ColombiaWithoutCityIdIsCityRequiredOnCityId()
    {
        var failure = await Fails(Ensure(Command(), Strict));

        Assert.Equal(CustomersErrorCodes.CityRequired, failure.Code);
        Assert.Equal(["CityId"], Keys(failure));
    }

    [Fact]
    public async Task AbroadWithoutCityNameIsCityNameRequiredOnCityName()
    {
        var failure = await Fails(Ensure(Command(type: "PASAPORTE", country: "ES"), Strict));

        Assert.Equal(CustomersErrorCodes.CityNameRequired, failure.Code);
        Assert.Equal(["CityName"], Keys(failure));
    }

    [Fact]
    public async Task CreatingWithoutDocumentFails()
    {
        var failure = await Fails(Ensure(Command(type: null, number: null, cityId: CityId), Strict, current: null));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, failure.Code);
    }

    [Fact]
    public async Task CompletingAnIncompleteWithoutDocumentFails()
    {
        var failure = await Fails(Ensure(Command(type: null, number: null, cityId: CityId), Strict, Incomplete()));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, failure.Code);
    }

    [Fact]
    public async Task APutThatLeavesAMissingDocumentUntouchedPasses()
    {
        var current = CompleteWith(null, Colombian());

        await Ensure(Command(type: null, number: null, cityId: CityId), Strict, current);
    }

    [Fact]
    public async Task APutThatClearsAnExistingDocumentFails()
    {
        var current = CompleteWith(Identification(IdentificationType.Cc, "1020304050"), Colombian());

        var failure = await Fails(Ensure(Command(type: null, number: null, cityId: CityId), Strict, current));

        Assert.Equal(CustomersErrorCodes.IdentificationRequired, failure.Code);
    }

    [Fact]
    public async Task APutThatChangesTheNumberWithoutTypeIsIncomplete()
    {
        var current = CompleteWith(null, Colombian());

        var failure = await Fails(Ensure(Command(type: null, number: "999", cityId: CityId), Strict, current));

        Assert.Equal(CustomersErrorCodes.IdentificationIncomplete, failure.Code);
        Assert.Equal(["IdentificationType"], Keys(failure));
    }

    [Fact]
    public async Task ResendingTheSameDocumentWithOtherCaseOrPaddingIsUnchanged()
    {
        var current = CompleteWith(
            Identification(IdentificationType.Cc, "1020304050"), ColombianWithoutCity());

        await Ensure(Command(type: " cc ", number: "1020304050 "), Strict, current);
    }

    [Fact]
    public async Task APutThatLeavesAMissingCityUntouchedPasses()
    {
        var current = CompleteWith(Identification(IdentificationType.Cc, "1020304050"), ColombianWithoutCity());

        await Ensure(Command(), Strict, current);
    }

    [Fact]
    public async Task APutThatClearsTheCityFails()
    {
        var current = CompleteWith(Identification(IdentificationType.Cc, "1020304050"), Colombian());

        var failure = await Fails(Ensure(Command(), Strict, current));

        Assert.Equal(CustomersErrorCodes.CityRequired, failure.Code);
    }

    [Fact]
    public async Task ChangingTheCountryCountsAsACityChange()
    {
        var current = CompleteWith(Identification(IdentificationType.Cc, "1020304050"), ColombianWithoutCity());

        var failure = await Fails(Ensure(Command(type: "PASAPORTE", country: "ES"), Strict, current));

        Assert.Equal(CustomersErrorCodes.CityNameRequired, failure.Code);
    }

    [Fact]
    public void ToIdentificationIsNullWhenBothFieldsAreBlank()
    {
        Assert.Null(CustomerMapping.ToIdentification(null, null));
        Assert.Null(CustomerMapping.ToIdentification("  ", "  "));
        Assert.Null(CustomerMapping.ToIdentification(null, "  "));
        Assert.NotNull(CustomerMapping.ToIdentification("CC", "1020304050"));
    }
}
