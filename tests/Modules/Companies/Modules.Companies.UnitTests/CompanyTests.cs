using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

/// <summary>
/// El agregado empresa. Lo que hace a sus cuentas bancarias vive aparte, en
/// <see cref="CompanyBankAccountTests"/>.
/// </summary>
public sealed class CompanyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static readonly Guid CityId =
        Guid.Parse("01a0272d-4c71-722b-abc0-5c2311ae384f");

    private static CompanyBankAccount[] Accounts(string accountNumber = "CTA-000123") =>
    [
        new()
        {
            BankName = "Bancolombia",
            AccountNumber = accountNumber,
            Currency = "COP"
        }
    ];

    private static Company Create(
        string name = "Andes Logistica S.A.S.",
        string taxId = "900.111.222-3",
        string country = "CO",
        CompanyDocumentType documentType = CompanyDocumentType.Nit,
        Guid? cityId = null,
        string? cityName = null,
        CompanyContactInfo? contact = null) =>
        Company.Create(
            CompanyId.New(),
            TenantId,
            name,
            Accounts(),
            taxId,
            country,
            documentType,
            cityId ?? (country == "CO" ? CityId : null),
            cityName,
            contact ?? CompanyContactInfo.Empty,
            Now);

    // Recortar es parte del invariante, no higiene del llamador: sin recortar, " Andes" y "Andes"
    // son dos nombres distintos para cualquier comparacion.
    [Fact]
    public void CreateTrimsTheIdentifyingFields()
    {
        var company = Create(name: "  Andes  ", taxId: " 900-1 ");

        Assert.Equal("Andes", company.Name);
        Assert.Equal("900-1", company.TaxId);
    }

    [Fact]
    public void CreateStartsActiveAtVersionOne()
    {
        var company = Create();

        Assert.True(company.IsActive);
        Assert.Equal(1, company.Version);
        Assert.Equal(Now, company.CreatedAt);
        Assert.Equal(Now, company.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankName(string name)
    {
        var exception = Assert.Throws<CompaniesDomainException>(() => Create(name: name));

        Assert.Equal("companies.company.name_required", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankTaxId(string taxId)
    {
        var exception = Assert.Throws<CompaniesDomainException>(() => Create(taxId: taxId));

        Assert.Equal("companies.company.tax_id_required", exception.Code);
    }

    // Los maximos espejan los anchos de columna: un valor demasiado largo tiene que salir como
    // 422 con codigo de dominio, no llegar a PostgreSQL y volver como 500.
    [Fact]
    public void CreateRejectsANameLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(name: new string('a', Company.NameMaxLength + 1)));

        Assert.Equal("companies.company.name_too_long", exception.Code);
    }

    [Fact]
    public void CreateRejectsATaxIdLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(taxId: new string('9', Company.TaxIdMaxLength + 1)));

        Assert.Equal("companies.company.tax_id_too_long", exception.Code);
    }

    [Fact]
    public void ContactInfoTrimsAndNormalizesBlankToNull()
    {
        var company = Create(contact: new CompanyContactInfo
        {
            Phone = "  310 555 1122  ",
            Email = "   ",
            Address = ""
        });

        Assert.Equal("310 555 1122", company.Phone);
        Assert.Null(company.Email);
        Assert.Null(company.Address);
    }

    // Dejar "Contacto@Andes.CO" y "contacto@andes.co" como dos valores distintos obliga a cada
    // consumidor a normalizar de nuevo, y basta con que uno se olvide para que la comparacion
    // falle. Mismo criterio con el que ProductDetails normaliza la moneda a mayusculas.
    [Fact]
    public void ContactInfoLowercasesTheEmail()
    {
        var company = Create(contact: new CompanyContactInfo { Email = "Contacto@Andes.CO" });

        Assert.Equal("contacto@andes.co", company.Email);
    }

    [Theory]
    [InlineData("no-arroba")]
    [InlineData("con espacio@andes.co")]
    [InlineData("@andes.co")]
    public void ContactInfoRejectsAnInvalidEmail(string email)
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(contact: new CompanyContactInfo { Email = email }));

        Assert.Equal("companies.company.email_invalid", exception.Code);
    }

    [Fact]
    public void ContactInfoRejectsAPhoneLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(contact: new CompanyContactInfo
            {
                Phone = new string('3', CompanyContactInfo.PhoneMaxLength + 1)
            }));

        Assert.Equal("companies.company.phone_too_long", exception.Code);
    }

    [Fact]
    public void ContactInfoRejectsAnAddressLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(contact: new CompanyContactInfo
            {
                Address = new string('c', CompanyContactInfo.AddressMaxLength + 1)
            }));

        Assert.Equal("companies.company.address_too_long", exception.Code);
    }

    // El PUT reemplaza el recurso entero: un campo ausente se **limpia**. Una implementacion que
    // ignore los null "para no pisar" deja campos imborrables y pasa todas las demas pruebas.
    // Es el caso CA-CAT-04-03 de catalogo, un modulo antes que este.
    [Fact]
    public void UpdateClearsTheOptionalFieldsThatArriveNull()
    {
        var company = Create(contact: new CompanyContactInfo
        {
            Phone = "310 555 1122",
            Email = "contacto@andes.co",
            Address = "Calle 80 # 45-12"
        });

        company.Update(
            "Andes Logistica S.A.S.",
            Accounts(),
            "900.111.222-3",
            "CO",
            CompanyDocumentType.Nit,
            CityId,
            null,
            CompanyContactInfo.Empty,
            Now.AddMinutes(5));

        Assert.Null(company.Phone);
        Assert.Null(company.Email);
        Assert.Null(company.Address);
    }

    [Fact]
    public void UpdateIncrementsTheConcurrencyToken()
    {
        var company = Create();
        var later = Now.AddMinutes(5);

        company.Update("Otro", Accounts("CTA-2"), "900-2", "CO", CompanyDocumentType.Nit, CityId, null, CompanyContactInfo.Empty, later);

        Assert.Equal(2, company.Version);
        Assert.Equal(later, company.UpdatedAt);
        Assert.Equal(Now, company.CreatedAt);
    }

    // Update valida todo antes de asignar nada. Sin eso, un nombre valido seguido de un NIT
    // invalido dejaba el nombre nuevo pegado en la instancia que EF sigue rastreando, aunque el
    // llamador se lleve un 422 que dice que no se guardo nada.
    [Fact]
    public void UpdateLeavesTheCompanyUntouchedWhenALaterFieldIsRejected()
    {
        var company = Create(name: "Andes");

        Assert.Throws<CompaniesDomainException>(() => company.Update(
            "Nombre nuevo",
            Accounts(),
            "   ",
            "CO",
            CompanyDocumentType.Nit,
            CityId,
            null,
            CompanyContactInfo.Empty,
            Now.AddMinutes(5)));

        Assert.Equal("Andes", company.Name);
        Assert.Equal(1, company.Version);
    }

    [Fact]
    public void UpdateRejectsAnInactiveCompany()
    {
        var company = Create();
        company.Deactivate(Now);

        var exception = Assert.Throws<CompaniesDomainException>(
            () => company.Update(
                "Otro", Accounts("CTA-2"), "900-2", "CO", CompanyDocumentType.Nit, CityId, null, CompanyContactInfo.Empty, Now));

        Assert.Equal("companies.company.inactive", exception.Code);
    }

    [Fact]
    public void DeactivateTwiceIsRejected()
    {
        var company = Create();
        company.Deactivate(Now);

        var exception = Assert.Throws<CompaniesDomainException>(() => company.Deactivate(Now));

        Assert.Equal("companies.company.already_inactive", exception.Code);
    }

    [Fact]
    public void ActivateAnAlreadyActiveCompanyIsRejected()
    {
        var company = Create();

        var exception = Assert.Throws<CompaniesDomainException>(() => company.Activate(Now));

        Assert.Equal("companies.company.already_active", exception.Code);
    }

    // Sin Activate, una empresa inactiva era terminal: Update abre con EnsureActive() y nada
    // devolvia IsActive a true. Es la misma vuelta que CAT-07 le agrego a producto.
    [Fact]
    public void ActivateRestoresEditability()
    {
        var company = Create();
        company.Deactivate(Now);

        company.Activate(Now.AddMinutes(1));
        company.Update(
            "Otro", Accounts("CTA-2"), "900-2", "CO", CompanyDocumentType.Nit, CityId, null, CompanyContactInfo.Empty, Now.AddMinutes(2));

        Assert.True(company.IsActive);
        Assert.Equal("Otro", company.Name);
    }

    // ---- País, tipo de documento y ciudad ----

    private static CompaniesDomainException Fails(Action action) =>
        Assert.Throws<CompaniesDomainException>(action);

    [Fact]
    public void ColombianCompanyRequiresCityId()
    {
        var exception = Fails(() => Company.Create(
            CompanyId.New(), TenantId, "Andes", Accounts(), "900-1", "CO",
            CompanyDocumentType.Nit, null, null, CompanyContactInfo.Empty, Now));

        Assert.Equal("companies.company.city_required", exception.Code);
    }

    [Fact]
    public void ColombianCompanyDropsCityName()
    {
        var company = Create(cityName: "Bogotá");

        Assert.Null(company.CityName);
        Assert.Equal(CityId, company.CityId);
    }

    [Fact]
    public void NonColombianCompanyRequiresCityName()
    {
        var exception = Fails(() => Create(
            country: "US", documentType: CompanyDocumentType.TaxId, cityName: "  "));

        Assert.Equal("companies.company.city_name_required", exception.Code);
    }

    [Fact]
    public void CityNameLongerThan120IsRejected()
    {
        var exception = Fails(() => Create(
            country: "US", documentType: CompanyDocumentType.TaxId, cityName: new string('a', 121)));

        Assert.Equal("companies.company.city_name_too_long", exception.Code);
    }

    [Fact]
    public void NonColombianCompanyDropsCityId()
    {
        var company = Create(
            country: "US", documentType: CompanyDocumentType.TaxId, cityId: Guid.NewGuid(), cityName: "Miami");

        Assert.Null(company.CityId);
        Assert.Equal("Miami", company.CityName);
    }

    [Fact]
    public void CountryIsNormalized()
    {
        var company = Create(
            country: " es ", documentType: CompanyDocumentType.Nif, cityName: "Madrid");

        Assert.Equal("ES", company.Country);
    }

    [Theory]
    [InlineData("Col")]
    [InlineData("C1")]
    [InlineData("")]
    public void AnInvalidCountryIsRejected(string country)
    {
        var exception = Fails(() => Create(country: country, cityName: "X"));

        Assert.Equal("companies.company.country_invalid", exception.Code);
    }

    [Fact]
    public void NifWithColombiaIsNotAllowed()
    {
        var exception = Fails(() => Create(documentType: CompanyDocumentType.Nif));

        Assert.Equal("companies.company.document_type_not_allowed", exception.Code);
    }

    [Fact]
    public void NitWithSpainIsNotAllowed()
    {
        var exception = Fails(() => Create(country: "ES", cityName: "Madrid"));

        Assert.Equal("companies.company.document_type_not_allowed", exception.Code);
    }

    [Theory]
    [InlineData("CO")]
    [InlineData("ES")]
    [InlineData("US")]
    public void PasaporteIsValidEverywhere(string country)
    {
        var company = Create(country: country, documentType: CompanyDocumentType.Pasaporte, cityName: "X");

        Assert.Equal(CompanyDocumentType.Pasaporte, company.DocumentType);
    }

    [Fact]
    public void TaxIdIsValidForUs()
    {
        var company = Create(country: "US", documentType: CompanyDocumentType.TaxId, cityName: "Miami");

        Assert.Equal(CompanyDocumentType.TaxId, company.DocumentType);
    }

    [Theory]
    [InlineData("CO")]
    [InlineData("ES")]
    public void TaxIdIsRejectedForColombiaAndSpain(string country)
    {
        var exception = Fails(() => Create(
            country: country, documentType: CompanyDocumentType.TaxId, cityName: "X"));

        Assert.Equal("companies.company.document_type_not_allowed", exception.Code);
    }

    [Fact]
    public void UpdateColombiaToUsClearsCityId()
    {
        var company = Create();

        company.Update(
            "Andes", Accounts(), "123", "US", CompanyDocumentType.TaxId, CityId, "Miami",
            CompanyContactInfo.Empty, Now.AddMinutes(5));

        Assert.Equal("US", company.Country);
        Assert.Null(company.CityId);
        Assert.Equal("Miami", company.CityName);
    }

    [Fact]
    public void FailedUpdateLeavesTheAggregateUnchanged()
    {
        var company = Create();

        Fails(() => company.Update(
            "Otro", Accounts(), "123", "ES", CompanyDocumentType.Nit, null, "Madrid",
            CompanyContactInfo.Empty, Now.AddMinutes(5)));

        Assert.Equal("CO", company.Country);
        Assert.Equal(CompanyDocumentType.Nit, company.DocumentType);
        Assert.Equal(CityId, company.CityId);
        Assert.Equal(1, company.Version);
    }
}
