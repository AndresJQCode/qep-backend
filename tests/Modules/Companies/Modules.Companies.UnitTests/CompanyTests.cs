using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

public sealed class CompanyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static Company Create(
        string name = "Andes Logistica S.A.S.",
        string accountNumber = "CTA-000123",
        string taxId = "900.111.222-3",
        CompanyContactInfo? contact = null) =>
        Company.Create(
            CompanyId.New(),
            TenantId,
            name,
            accountNumber,
            taxId,
            contact ?? CompanyContactInfo.Empty,
            Now);

    // El indice unico trata " CTA-1" y "CTA-1" como dos numeros de cuenta distintos, cosa que
    // nadie leyendo la lista haria. Recortar es parte del invariante, no higiene del llamador.
    [Fact]
    public void CreateTrimsTheIdentifyingFields()
    {
        var company = Create(name: "  Andes  ", accountNumber: " CTA-1 ", taxId: " 900-1 ");

        Assert.Equal("Andes", company.Name);
        Assert.Equal("CTA-1", company.AccountNumber);
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
    public void CreateRejectsABlankAccountNumber(string accountNumber)
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(accountNumber: accountNumber));

        Assert.Equal("companies.company.account_number_required", exception.Code);
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
    public void CreateRejectsAnAccountNumberLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(accountNumber: new string('1', Company.AccountNumberMaxLength + 1)));

        Assert.Equal("companies.company.account_number_too_long", exception.Code);
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
            "CTA-000123",
            "900.111.222-3",
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

        company.Update("Otro", "CTA-2", "900-2", CompanyContactInfo.Empty, later);

        Assert.Equal(2, company.Version);
        Assert.Equal(later, company.UpdatedAt);
        Assert.Equal(Now, company.CreatedAt);
    }

    [Fact]
    public void UpdateRejectsAnInactiveCompany()
    {
        var company = Create();
        company.Deactivate(Now);

        var exception = Assert.Throws<CompaniesDomainException>(
            () => company.Update("Otro", "CTA-2", "900-2", CompanyContactInfo.Empty, Now));

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
        company.Update("Otro", "CTA-2", "900-2", CompanyContactInfo.Empty, Now.AddMinutes(2));

        Assert.True(company.IsActive);
        Assert.Equal("Otro", company.Name);
    }
}
