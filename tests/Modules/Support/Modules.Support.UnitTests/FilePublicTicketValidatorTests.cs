using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

/// <summary>
/// El validador del radicado del formulario público (plan fase 3, Task 6; F18 a F20). Los campos los exige según
/// <see cref="SupportFormFields"/> y la versión del formulario, sin repetir la regla; las claves del mapa <c>errors</c> son las del
/// cuerpo (<c>Requester.DocumentType</c>…), que es lo que la página sabe marcar.
/// </summary>
public sealed class FilePublicTicketValidatorTests
{
    private static readonly Guid TypeId = Guid.CreateVersion7();

    private static readonly Guid Bogota = Guid.CreateVersion7();

    private static SupportFormVersion Form(bool showDocument = true, bool businessNameRequired = true) =>
        SupportFormVersion.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 1, isPublished: true, showDocument, showAddress: true, showLandline: true,
            businessNameRequired, null, null, null, Guid.CreateVersion7(), DateTimeOffset.UtcNow);

    private static SupportPublicRequesterRequest Natural(
        string? documentType = "CC",
        string? documentNumber = "1020304050",
        string? firstNames = "Ana",
        string? lastNames = "Gómez",
        string? businessName = null,
        string? mobilePhone = AutoMobile,
        string? country = "CO",
        Guid? cityId = null,
        string? cityName = null,
        string? email = "ana@example.com") =>
        new(documentType, documentNumber, firstNames, lastNames, businessName, null, null,
            mobilePhone == AutoMobile ? (country == "ES" ? "612345678" : "3001234567") : mobilePhone, country,
            cityId ?? (country == "CO" ? Bogota : null), cityName, email);

    // Un celular válido del país del solicitante (ver FakePhones).
    private const string AutoMobile = "auto";

    private static FilePublicTicketCommand Command(
        SupportPublicRequesterRequest? requester,
        bool anonymous = false,
        bool acceptsDataPolicy = true,
        string? subject = "No me llegó el pedido",
        string? description = "Pagué el lunes y todavía no llega.",
        SupportFormVersion? form = null) =>
        new FilePublicTicketCommand(
            "mi-empresa", "203.0.113.7", TypeId, subject, description, anonymous, requester, acceptsDataPolicy, 1, 1, "token")
        {
            Form = form ?? Form(),
        };

    private static List<string> Errors(FilePublicTicketCommand command) =>
        new FilePublicTicketValidator(new FakeCountries(), new FakePhones()).Validate(command)
            .Errors.Select(error => error.PropertyName).Distinct().ToList();

    [Fact]
    public void AValidNaturalRequesterPasses() => Assert.Empty(Errors(Command(Natural())));

    [Fact]
    public void ANaturalRequesterNeedsNamesMobileCountryCityAndEmail()
    {
        var errors = Errors(Command(Natural(firstNames: " ", lastNames: null, mobilePhone: null, country: null, email: "")));

        Assert.Contains("Requester.FirstNames", errors);
        Assert.Contains("Requester.LastNames", errors);
        Assert.Contains("Requester.MobilePhone", errors);
        Assert.Contains("Requester.Country", errors);
        Assert.Contains("Requester.Email", errors);
        Assert.DoesNotContain("Requester.BusinessName", errors);
    }

    [Fact]
    public void AMobileThatDoesNotNormalizeIsAnError() =>
        Assert.Equal(["Requester.MobilePhone"], Errors(Command(Natural(mobilePhone: "12"))));

    [Fact]
    public void ANitRequesterNeedsBusinessNameOnlyWhenTheFormSaysSo()
    {
        var nit = Natural(documentType: "NIT", documentNumber: "900123456", firstNames: null, lastNames: null, businessName: null);

        var required = Errors(Command(nit, form: Form(businessNameRequired: true)));
        var optional = Errors(Command(nit, form: Form(businessNameRequired: false)));

        Assert.Equal(["Requester.BusinessName"], required);
        Assert.Empty(optional);
    }

    [Fact]
    public void TheDocumentTypeMustBeAllowedForTheRequesterCountry()
    {
        Assert.Equal(["Requester.DocumentType"], Errors(Command(Natural(documentType: "NIF"))));
        Assert.Empty(Errors(Command(Natural(documentType: "NIF", country: "ES", cityName: "Madrid"))));
    }

    [Fact]
    public void WithoutTheDocumentPairTheDocumentIsNotRequired()
    {
        var errors = Errors(Command(Natural(documentType: null, documentNumber: null), form: Form(showDocument: false)));

        Assert.Empty(errors);
    }

    [Fact]
    public void ColombiaNeedsACityIdOtherCountriesACityName()
    {
        var colombia = new SupportPublicRequesterRequest(
            "CC", "1020304050", "Ana", "Gómez", null, null, null, "3001234567", "CO", null, "Bogotá", "ana@example.com");
        var spain = Natural(documentType: "NIE", country: "ES", cityName: null);
        var tooLong = Natural(documentType: "NIE", country: "ES", cityName: new string('a', 101));

        Assert.Equal(["Requester.CityId"], Errors(Command(colombia)));
        Assert.Equal(["Requester.CityName"], Errors(Command(spain)));
        Assert.Equal(["Requester.CityName"], Errors(Command(tooLong)));
        Assert.Empty(Errors(Command(Natural(documentType: "NIE", country: "ES", cityName: new string('a', 100)))));
    }

    [Fact]
    public void AnUnknownCountryIsAnError() =>
        Assert.Contains("Requester.Country", Errors(Command(Natural(country: "ZZ", cityName: "X"))));

    [Fact]
    public void AnAnonymousBodyMayNotCarryARequester()
    {
        Assert.Equal(["Requester"], Errors(Command(Natural(), anonymous: true, acceptsDataPolicy: false)));
        Assert.Empty(Errors(Command(null, anonymous: true, acceptsDataPolicy: false)));
    }

    [Fact]
    public void ANamedBodyNeedsARequester() => Assert.Equal(["Requester"], Errors(Command(null)));

    [Fact]
    public void SubjectAndDescriptionLimits()
    {
        Assert.Contains("Subject", Errors(Command(Natural(), subject: " ")));
        Assert.Contains("Subject", Errors(Command(Natural(), subject: new string('a', 201))));
        Assert.Contains("Description", Errors(Command(Natural(), description: null)));
        Assert.Contains("Description", Errors(Command(Natural(), description: new string('a', 5001))));
        Assert.Empty(Errors(Command(Natural(), subject: new string('a', 200), description: new string('a', 5000))));
    }

    [Fact]
    public void TheConsentMustBeAccepted() =>
        Assert.Equal(["AcceptsDataPolicy"], Errors(Command(Natural(), acceptsDataPolicy: false)));

    [Fact]
    public void TheTypeAndTheVersionsAreRequired()
    {
        var command = new FilePublicTicketCommand(
            "mi-empresa", "203.0.113.7", null, "Asunto", "Descripción", false, Natural(), true, null, null, "token")
        {
            Form = Form(),
        };

        var errors = Errors(command);

        Assert.Contains("TicketTypeId", errors);
        Assert.Contains("LegalTextVersion", errors);
        Assert.Contains("FormVersion", errors);
    }

    private sealed class FakeCountries : ISupportCountries
    {
        public bool IsKnown(string countryCode) => countryCode is "CO" or "ES" or "US";
    }

    // Diez dígitos en Colombia, nueve en España: lo demás no es un celular.
    private sealed class FakePhones : ISupportPhoneNormalizer
    {
        public string? ToE164(string raw, string countryCode) => (countryCode, raw.Length) switch
        {
            ("CO", 10) => "+57" + raw,
            ("ES", 9) => "+34" + raw,
            _ => null,
        };
    }
}
