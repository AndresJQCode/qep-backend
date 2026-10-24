using Modules.Suppliers.Domain;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// El agregado <see cref="Supplier"/> (SUP-01): normalización, ubicación por país, plazo de pago,
/// topes de colecciones y el todo-o-nada de <see cref="Supplier.Update"/>.
/// </summary>
public sealed class SupplierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = Now.AddHours(1);

    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static readonly Guid CityId = Guid.Parse("01a0272d-4c71-722b-abc0-5c2311ae384f");

    // País CO devuelve un E.164 reconocible; cualquier otro, null: el normalizador real también
    // devuelve null cuando no puede parsear, y el agregado no debe fallar por eso.
    private sealed class FakePhones : ISupplierPhoneNormalizer
    {
        public string? ToE164(string? phone, string? country) =>
            phone is null || country != "CO" ? null : "+57" + phone.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static readonly FakePhones Phones = new();

    private static SupplierBankAccount Account(string bank = "Bancolombia", string number = "CTA-1", string currency = "COP") =>
        new() { BankName = bank, AccountNumber = number, Currency = currency };

    private static SupplierWriteData Data(
        string? name = "Ferretería Central",
        string? country = "CO",
        Guid? cityId = null,
        string? cityName = null,
        string? phone = "3001234567",
        string? email = "ventas@ferreteria.co",
        string? address = "Calle 1 # 2-3",
        int paymentTermDays = 30,
        IReadOnlyList<SupplierBankAccount>? bankAccounts = null,
        IReadOnlyList<SupplierContact>? contacts = null,
        string? identificationType = "NIT",
        string? identificationNumber = "900.123.456-7") =>
        new(
            name,
            "Ferretería Central S.A.S.",
            identificationType,
            identificationNumber,
            country,
            string.Equals(country, "CO", StringComparison.OrdinalIgnoreCase) ? cityId ?? CityId : cityId,
            cityName,
            phone,
            email,
            address,
            paymentTermDays,
            "Ferretería",
            "Entrega los martes.",
            bankAccounts ?? [],
            contacts ?? []);

    private static Supplier Create(SupplierWriteData? data = null) =>
        Supplier.Create(TenantId, data ?? Data(), Phones, Now);

    private static string CodeOf(Action act) =>
        Assert.Throws<SuppliersDomainException>(act).Code;

    [Fact]
    public void CreateTrimsNamesUppercasesCountryAndLowercasesEmail()
    {
        var supplier = Create(Data(name: "  Ferretería ", country: "co", email: "A@B.CO"));

        Assert.Equal("Ferretería", supplier.Name);
        Assert.Equal("CO", supplier.Contact.Country);
        Assert.Equal("a@b.co", supplier.Contact.Email);
        Assert.Equal(TenantId, supplier.TenantId);
        Assert.True(supplier.IsActive);
        Assert.Equal(1, supplier.Version);
        Assert.Equal(Now, supplier.CreatedAt);
        Assert.Equal(Now, supplier.UpdatedAt);
        Assert.Equal(IdentificationType.Nit, supplier.Identification.Type);
    }

    // D-4: un proveedor puede cargarse sin teléfono, correo ni dirección.
    [Fact]
    public void CreateWithoutPhoneEmailOrAddressSucceeds()
    {
        var supplier = Create(Data(phone: null, email: null, address: null));

        Assert.Null(supplier.Contact.Phone);
        Assert.Null(supplier.Contact.Email);
        Assert.Null(supplier.Contact.Address);
        Assert.Null(supplier.PhoneE164);
    }

    [Fact]
    public void CreateComputesPhoneE164WithTheCountry()
    {
        var supplier = Create(Data(phone: "300 123"));

        Assert.Equal("300 123", supplier.Contact.Phone);
        Assert.Equal("+57300123", supplier.PhoneE164);
    }

    [Fact]
    public void CreateWithBlankNameThrowsNameRequired() =>
        Assert.Equal("suppliers.supplier.name_required", CodeOf(() => Create(Data(name: "  "))));

    [Fact]
    public void CreateWithNameOver160ThrowsNameTooLong() =>
        Assert.Equal("suppliers.supplier.name_too_long", CodeOf(() => Create(Data(name: new string('a', 161)))));

    [Fact]
    public void CreateInColombiaWithoutCityIdThrowsCityRequired()
    {
        var data = Data() with { CityId = null };

        Assert.Equal("suppliers.supplier.city_required", CodeOf(() => Create(data)));
    }

    [Fact]
    public void CreateOutsideColombiaWithoutCityNameThrowsCityNameRequired() =>
        Assert.Equal(
            "suppliers.supplier.city_name_required",
            CodeOf(() => Create(Data(country: "US", cityName: "  "))));

    [Fact]
    public void CreateInColombiaClearsCityNameAndOutsideClearsCityId()
    {
        var colombian = Create(Data(country: "CO", cityName: "Bogotá"));
        var foreign = Create(Data(country: "US", cityName: "Miami", cityId: CityId));

        Assert.Equal(CityId, colombian.CityId);
        Assert.Null(colombian.CityName);
        Assert.Null(foreign.CityId);
        Assert.Equal("Miami", foreign.CityName);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    public void CreateValidatesThePaymentTermRange(int days, bool valid)
    {
        if (valid)
        {
            Assert.Equal(days, Create(Data(paymentTermDays: days)).PaymentTermDays);
        }
        else
        {
            Assert.Equal(
                "suppliers.supplier.payment_term_invalid",
                CodeOf(() => Create(Data(paymentTermDays: days))));
        }
    }

    [Fact]
    public void CreateWithTwentyOneBankAccountsThrowsTooMany()
    {
        var accounts = Enumerable.Range(1, 21).Select(i => Account(number: $"CTA-{i}")).ToList();

        Assert.Equal(
            "suppliers.supplier.bank_accounts_too_many",
            CodeOf(() => Create(Data(bankAccounts: accounts))));
    }

    [Fact]
    public void CreateWithDuplicatedBankAccountThrows() =>
        Assert.Equal(
            "suppliers.supplier.bank_account_duplicated",
            CodeOf(() => Create(Data(bankAccounts: [Account(bank: "Bancolombia"), Account(bank: " BANCOLOMBIA ")]))));

    [Fact]
    public void CreateWithElevenContactsThrowsTooMany()
    {
        var contacts = Enumerable.Range(1, 11)
            .Select(i => SupplierContact.Create($"Contacto {i}", null, null, null))
            .ToList();

        Assert.Equal(
            "suppliers.supplier.contacts_too_many",
            CodeOf(() => Create(Data(contacts: contacts))));
    }

    // D-8, D-9: ni las cuentas ni los contactos son obligatorios.
    [Fact]
    public void CreateWithZeroBankAccountsAndZeroContactsSucceeds()
    {
        var supplier = Create();

        Assert.Empty(supplier.BankAccounts);
        Assert.Empty(supplier.Contacts);
    }

    [Fact]
    public void CreateKeepsBankAccountsAndContactsNormalized()
    {
        var supplier = Create(Data(
            bankAccounts: [Account(bank: " Davivienda ", currency: "usd")],
            contacts: [SupplierContact.Create(" Ana ", " Compras ", null, "ANA@X.CO")]));

        Assert.Equal("Davivienda", Assert.Single(supplier.BankAccounts).BankName);
        Assert.Equal("USD", supplier.BankAccounts[0].Currency);
        Assert.Equal("ana@x.co", Assert.Single(supplier.Contacts).Email);
    }

    [Theory]
    [InlineData("businessName", "suppliers.supplier.business_name_too_long")]
    [InlineData("category", "suppliers.supplier.category_too_long")]
    [InlineData("notes", "suppliers.supplier.notes_too_long")]
    public void CreateRejectsOverlongOptionalText(string field, string code)
    {
        var data = field switch
        {
            "businessName" => Data() with { BusinessName = new string('a', Supplier.BusinessNameMaxLength + 1) },
            "category" => Data() with { Category = new string('a', Supplier.CategoryMaxLength + 1) },
            _ => Data() with { Notes = new string('a', Supplier.NotesMaxLength + 1) }
        };

        Assert.Equal(code, CodeOf(() => Create(data)));
    }

    [Fact]
    public void UpdateThatFailsLeavesTheAggregateUntouched()
    {
        var supplier = Create();

        var code = CodeOf(() => supplier.Update(Data(name: " "), Phones, Later));

        Assert.Equal("suppliers.supplier.name_required", code);
        Assert.Equal("Ferretería Central", supplier.Name);
        Assert.Equal(Now, supplier.UpdatedAt);
        Assert.Equal(1, supplier.Version);
    }

    [Fact]
    public void UpdateReplacesEverythingAndBumpsVersion()
    {
        var supplier = Create(Data(bankAccounts: [Account()]));

        supplier.Update(Data(name: "Nuevo", bankAccounts: [], paymentTermDays: 60), Phones, Later);

        Assert.Equal("Nuevo", supplier.Name);
        Assert.Equal(60, supplier.PaymentTermDays);
        Assert.Empty(supplier.BankAccounts);
        Assert.Equal(2, supplier.Version);
        Assert.Equal(Later, supplier.UpdatedAt);
        Assert.Equal(Now, supplier.CreatedAt);
    }

    [Fact]
    public void UpdateRecomputesPhoneE164WithTheNewCountry()
    {
        var supplier = Create(Data(phone: "3001234567"));
        Assert.Equal("+573001234567", supplier.PhoneE164);

        supplier.Update(Data(country: "US", cityName: "Miami", phone: "3001234567"), Phones, Later);

        Assert.Null(supplier.PhoneE164);
    }

    [Fact]
    public void DeactivateTwiceThrowsAlreadyInactive()
    {
        var supplier = Create();

        supplier.Deactivate(Later);

        Assert.False(supplier.IsActive);
        Assert.Equal(2, supplier.Version);
        Assert.Equal("suppliers.supplier.already_inactive", CodeOf(() => supplier.Deactivate(Later)));
    }

    [Fact]
    public void ActivateAnActiveSupplierThrowsAlreadyActive()
    {
        var supplier = Create();

        Assert.Equal("suppliers.supplier.already_active", CodeOf(() => supplier.Activate(Later)));

        supplier.Deactivate(Later);
        supplier.Activate(Later.AddHours(1));
        Assert.True(supplier.IsActive);
        Assert.Equal(3, supplier.Version);
    }

    [Fact]
    public void IdentificationTypeParserAcceptsWireValuesIgnoringCase()
    {
        Assert.Equal(IdentificationType.Nit, IdentificationTypeParser.Parse("nit"));
        Assert.Equal(IdentificationType.Cc, IdentificationTypeParser.Parse("CC"));
        Assert.Equal(IdentificationType.Ce, IdentificationTypeParser.Parse("Ce"));
        Assert.Equal(IdentificationType.Pasaporte, IdentificationTypeParser.Parse("PASAPORTE"));
        Assert.Equal("PASAPORTE", IdentificationType.Pasaporte.ToWire());
        Assert.Equal(
            "suppliers.supplier.identification_type_invalid",
            CodeOf(() => IdentificationTypeParser.Parse("RUT")));
        Assert.Equal(
            "suppliers.supplier.identification_type_invalid",
            CodeOf(() => Create(Data(identificationType: "RUT"))));
    }

    [Fact]
    public void IdentificationNumberKeepsDotsAndDashesAndRejectsOver32()
    {
        Assert.Equal("900.123.456-7", Create().Identification.Number);
        Assert.Equal(
            "suppliers.supplier.identification_number_too_long",
            CodeOf(() => Create(Data(identificationNumber: new string('1', 33)))));
        Assert.Equal(
            "suppliers.supplier.identification_number_required",
            CodeOf(() => Create(Data(identificationNumber: " "))));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("COL")]
    [InlineData("C1")]
    [InlineData(null)]
    public void CreateWithInvalidCountryThrows(string? country) =>
        Assert.Equal(
            "suppliers.supplier.country_invalid",
            CodeOf(() => Create(Data(country: country))));

    [Fact]
    public void CreateWithInvalidEmailThrows() =>
        Assert.Equal(
            "suppliers.supplier.email_invalid",
            CodeOf(() => Create(Data(email: "no-es-correo"))));
}
