using Modules.Wholesale.Application;

namespace Modules.Wholesale.UnitTests;

/// <summary>Las claves del 422 de distribuidores del contrato: <c>Email</c>, <c>BusinessName</c>, <c>IdentificationNumber</c>,
/// <c>ContactName</c>, <c>PriceListId</c>, <c>AdvisorId</c>, <c>BillingCompanyId</c> y <c>Status</c>.</summary>
public sealed class DistributorWriteRulesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static InviteDistributorCommand Invite(
        string email = "ana@distri.co",
        string businessName = "Distribuciones Norte SAS",
        string identificationNumber = "900123456",
        string contactName = "Ana Pérez",
        string country = "CO",
        string identificationType = "NIT") =>
        new(Tenant, email, businessName, country, identificationType, identificationNumber, contactName, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), "trace")
        {
            // La ciudad que el país exige: así los casos que no hablan de ella no fallan por ella.
            CityId = Guid.CreateVersion7(),
            CityName = "Madrid",
        };

    [Fact]
    public void AColombianInvitationNeedsTheCityId()
    {
        Assert.Equal(["CityId"], Failed(Invite() with { CityId = null }));
    }

    [Fact]
    public void AnEmptyCityIdIsMissing()
    {
        Assert.Equal(["CityId"], Failed(Invite() with { CityId = Guid.Empty }));
    }

    [Fact]
    public void AnInvitationOutsideColombiaNeedsTheCityName()
    {
        Assert.Equal(["CityName"], Failed(Invite(country: "ES", identificationType: "NIF") with { CityName = null }));
        Assert.Equal(["CityName"], Failed(Invite(country: "ES", identificationType: "NIF") with { CityName = "  " }));
        Assert.Empty(Failed(Invite(country: "ES", identificationType: "NIF") with { CityId = null }));
    }

    [Fact]
    public void TheCityNameHasAtMost120()
    {
        Assert.Empty(Failed(Invite() with { CityName = new string('a', 120) }));
        Assert.Equal(["CityName"], Failed(Invite() with { CityName = new string('a', 121) }));
    }

    [Fact]
    public void TheAddressIsOptionalAndHasAtMost200()
    {
        Assert.Empty(Failed(Invite() with { Address = null }));
        Assert.Empty(Failed(Invite() with { Address = new string('a', 200) }));
        Assert.Equal(["Address"], Failed(Invite() with { Address = new string('a', 201) }));
    }

    [Fact]
    public void AColombianInvitationDoesNotNeedTheCityName()
    {
        Assert.Empty(Failed(Invite() with { CityName = null }));
    }

    [Fact]
    public void ABadCountryOnlyFailsOnCountry()
    {
        // Sin un país válido no hay carril de ciudad que exigir (igual que CompanyWriteRules).
        Assert.Equal(["Country"], Failed(Invite(country: "C") with { CityId = null, CityName = null }));
    }

    private static string[] Failed(InviteDistributorCommand command) =>
        new InviteDistributorValidator().Validate(command).Errors.Select(error => error.PropertyName).Distinct().ToArray();

    [Fact]
    public void InviteRequiresAValidEmailAndTheTextLimits()
    {
        Assert.Empty(Failed(Invite()));
        Assert.Empty(Failed(Invite(email: "  Ana@Distri.CO  ")));
        Assert.Equal(["Email"], Failed(Invite(email: "ana-distri.co")));
        Assert.Equal(["Email"], Failed(Invite(email: "  ")));
        Assert.Equal(["Email"], Failed(Invite(email: new string('a', 245) + "@distri.co")));
        Assert.Equal(["BusinessName"], Failed(Invite(businessName: new string('a', 161))));
        Assert.Equal(["BusinessName"], Failed(Invite(businessName: " ")));
        Assert.Empty(Failed(Invite(businessName: " " + new string('a', 160) + " ")));
        Assert.Equal(["IdentificationNumber"], Failed(Invite(identificationNumber: new string('1', 33))));
        Assert.Equal(["IdentificationNumber"], Failed(Invite(identificationNumber: "")));
        Assert.Equal(["ContactName"], Failed(Invite(contactName: new string('a', 161))));
        Assert.Equal(["ContactName"], Failed(Invite(contactName: "")));
    }

    [Fact]
    public void InviteRequiresACountryAndATypeAllowedForIt()
    {
        Assert.Empty(Failed(Invite()));
        Assert.Empty(Failed(Invite(country: "es", identificationType: "nif")));
        Assert.Empty(Failed(Invite(country: "FR", identificationType: "TAX_ID")));
        Assert.Equal(["Country"], Failed(Invite(country: "")));
        Assert.Equal(["IdentificationType"], Failed(Invite(identificationType: "")));
        Assert.Equal(["IdentificationType"], Failed(Invite(country: "CO", identificationType: "NIF")));
        Assert.Equal(["IdentificationType"], Failed(Invite(country: "FR", identificationType: "NIT")));
    }

    [Fact]
    public void InviteRequiresTheCountryToBeTwoLettersAndJudgesTheTypeOnlyAgainstARealOne()
    {
        // Un país mal escrito falla sólo en el país: el tipo no se juzga contra un código que no existe.
        Assert.Equal(["Country"], Failed(Invite(country: "12")));
        Assert.Equal(["Country"], Failed(Invite(country: "COL")));
        Assert.Equal(["Country"], Failed(Invite(country: "C ")));

        var errors = new InviteDistributorValidator().Validate(Invite(country: "CO", identificationType: "NIF")).Errors;
        Assert.Equal("The identification type is not valid for the country.", Assert.Single(errors).ErrorMessage);
    }

    [Fact]
    public void InviteAndUpdateRequireTheThreeReferences()
    {
        var invite = new InviteDistributorCommand(
            Tenant, "ana@distri.co", "Norte", "CO", "NIT", "900123456", "Ana", Guid.Empty, Guid.Empty, Guid.Empty, "trace",
            CityId: Guid.CreateVersion7());
        var update = new UpdateDistributorCommand(Tenant, Guid.CreateVersion7(), 1, Guid.Empty, Guid.Empty, Guid.Empty);

        Assert.Equal(["PriceListId", "AdvisorId", "BillingCompanyId"], Failed(invite));
        Assert.Equal(
            ["PriceListId", "AdvisorId", "BillingCompanyId"],
            new UpdateDistributorValidator().Validate(update).Errors.Select(error => error.PropertyName).Distinct());
    }

    // Spec 2026-11-18: el teléfono se recorta y llega con a lo sumo 32 caracteres; la conexión vacía sólo cuenta con teléfono.
    [Fact]
    public void ThePhoneHasAtMost32CharactersAndAnEmptyConnectionOnlyCountsWithAPhone()
    {
        Assert.Empty(Failed(Invite() with { Phone = " " + new string('1', 32) + " ", WhatsAppConnectionId = Guid.CreateVersion7() }));
        Assert.Equal(["Phone"], Failed(Invite() with { Phone = new string('1', 33) }));
        Assert.Equal(["WhatsAppConnectionId"], Failed(Invite() with { Phone = "3001234567", WhatsAppConnectionId = Guid.Empty }));
        Assert.Empty(Failed(Invite() with { Phone = "  ", WhatsAppConnectionId = Guid.Empty }));

        var update = new UpdateDistributorCommand(
            Tenant, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new string('1', 33), Guid.Empty);
        Assert.Equal(
            ["Phone", "WhatsAppConnectionId"],
            new UpdateDistributorValidator().Validate(update).Errors.Select(error => error.PropertyName).Distinct());
    }

    [Theory]
    [InlineData("paused", false)]
    [InlineData("1", false)]
    [InlineData("active", true)]
    [InlineData("INVITED", true)]
    [InlineData("Blocked", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void ListRejectsAnUnknownStatusAndAcceptsAnyCase(string? status, bool valid)
    {
        var errors = new ListDistributorsValidator()
            .Validate(new ListDistributorsQuery(Tenant, status, null, 1, 50))
            .Errors.Select(error => error.PropertyName).ToArray();

        Assert.Equal(valid ? [] : ["Status"], errors);
    }
}
