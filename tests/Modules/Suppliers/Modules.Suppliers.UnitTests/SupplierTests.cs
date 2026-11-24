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

    // El campo inválido va al final del orden de normalización y todo lo anterior es válido y
    // distinto: una implementación que asigna campo por campo mientras valida pasaría una prueba
    // que falle en el nombre, pero aquí deja el nombre nuevo ya escrito.
    [Theory]
    [InlineData("contacts", "suppliers.supplier.contacts_too_many")]
    [InlineData("paymentTerm", "suppliers.supplier.payment_term_invalid")]
    public void UpdateThatFailsLateLeavesTheAggregateUntouched(string lateFailure, string expectedCode)
    {
        var originalContacts = new[] { SupplierContact.Create("Ana", null, null, null) };
        var supplier = Create(Data(bankAccounts: [Account()], contacts: originalContacts));
        var name = supplier.Name;
        var contact = supplier.Contact;
        var bankAccounts = supplier.BankAccounts.ToList();
        var contacts = supplier.Contacts.ToList();
        var paymentTermDays = supplier.PaymentTermDays;
        var category = supplier.Category;

        var tooManyContacts = Enumerable.Range(1, 11)
            .Select(i => SupplierContact.Create($"Contacto {i}", null, null, null))
            .ToList();
        var changed = Data(
            name: "Nombre Nuevo",
            country: "US",
            cityName: "Miami",
            email: "nuevo@otro.co",
            paymentTermDays: lateFailure == "paymentTerm" ? 366 : 90,
            bankAccounts: [Account(bank: "Davivienda", number: "CTA-9")],
            contacts: lateFailure == "contacts" ? tooManyContacts : [SupplierContact.Create("Luis", null, null, null)]);

        Assert.Equal(expectedCode, CodeOf(() => supplier.Update(changed, Phones, Later)));

        Assert.Equal(name, supplier.Name);
        Assert.Equal(contact, supplier.Contact);
        Assert.Equal(bankAccounts, supplier.BankAccounts);
        Assert.Equal(contacts, supplier.Contacts);
        Assert.Equal(paymentTermDays, supplier.PaymentTermDays);
        Assert.Equal(category, supplier.Category);
        Assert.Equal(1, supplier.Version);
        Assert.Equal(Now, supplier.UpdatedAt);
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

    // Un mayorista puede ser de cualquier país: el proveedor vinculado acepta los siete códigos de documento del producto.
    [Theory]
    [InlineData("NIF", IdentificationType.Nif)]
    [InlineData("NIE", IdentificationType.Nie)]
    [InlineData("TAX_ID", IdentificationType.TaxId)]
    public void IdentificationTypeParserAcceptsTheForeignDocumentTypes(string wire, IdentificationType expected)
    {
        Assert.Equal(expected, IdentificationTypeParser.Parse(wire.ToLowerInvariant()));
        Assert.Equal(wire, expected.ToWire());
        Assert.Contains(wire, IdentificationTypeParser.SupportedWireValues);
        Assert.Equal(expected, Create(Data(identificationType: wire)).Identification.Type);
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

    // Spec 2026-11-17 (mayorista), D-8: el proveedor que representa a un mayorista en QEP.
    private static readonly Guid Wholesaler = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    [Fact]
    public void CreateLinkedSetsTheLinkedTenant()
    {
        var supplier = Supplier.CreateLinked(TenantId, Wholesaler, Data(), Phones, Now);

        Assert.Equal((Wholesaler, TenantId, 1, true), (supplier.LinkedTenantId, supplier.TenantId, supplier.Version, supplier.IsActive));
        Assert.Equal("900.123.456-7", supplier.Identification.Number);
        Assert.Null(Create().LinkedTenantId);
    }

    [Fact]
    public void UpdateOfALinkedSupplierCannotChangeItsIdentification()
    {
        var supplier = Supplier.CreateLinked(TenantId, Wholesaler, Data(), Phones, Now);

        Assert.Equal(
            "suppliers.supplier.linked_identity_locked",
            CodeOf(() => supplier.Update(Data(identificationNumber: "900.999.999-9"), Phones, Later)));
        Assert.Equal(
            "suppliers.supplier.linked_identity_locked",
            CodeOf(() => supplier.Update(Data(identificationType: "CC"), Phones, Later)));
        Assert.Equal((1, "900.123.456-7"), (supplier.Version, supplier.Identification.Number));

        // Lo demás sigue libre, y el mismo documento con espacios alrededor no es un cambio.
        supplier.Update(Data(name: "Distribuciones del Norte", identificationNumber: " 900.123.456-7 "), Phones, Later);

        Assert.Equal(("Distribuciones del Norte", 2), (supplier.Name, supplier.Version));
    }

    [Fact]
    public void UpdateOfAnUnlinkedSupplierStillChangesItsIdentification()
    {
        var supplier = Create();

        supplier.Update(Data(identificationNumber: "900.999.999-9"), Phones, Later);

        Assert.Equal("900.999.999-9", supplier.Identification.Number);
    }

    [Fact]
    public void LinkToAnUnlinkedSupplierSetsTheLinkAndBumpsTheVersion()
    {
        var supplier = Create();

        supplier.LinkTo(Wholesaler, Later);

        Assert.Equal((Wholesaler, 2, Later), (supplier.LinkedTenantId, supplier.Version, supplier.UpdatedAt));
    }

    [Fact]
    public void LinkToAnotherTenantThrowsLinkedElsewhere()
    {
        var supplier = Supplier.CreateLinked(TenantId, Wholesaler, Data(), Phones, Now);

        Assert.Equal(
            "suppliers.supplier.linked_elsewhere",
            CodeOf(() => supplier.LinkTo(Guid.CreateVersion7(), Later)));
        Assert.Equal((Wholesaler, 1), (supplier.LinkedTenantId, supplier.Version));
    }

    [Fact]
    public void LinkToTheSameTenantIsANoOp()
    {
        var supplier = Supplier.CreateLinked(TenantId, Wholesaler, Data(), Phones, Now);

        supplier.LinkTo(Wholesaler, Later);

        Assert.Equal((Wholesaler, 1, Now), (supplier.LinkedTenantId, supplier.Version, supplier.UpdatedAt));
    }

    // ---- Region (provincia guardada, spec 2026-11-23) ----

    private static readonly Guid MadridRegionId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");

    [Fact]
    public void ColombianSupplierWithRegionIsRejected() =>
        Assert.Equal(
            "suppliers.supplier.region_lane_invalid",
            CodeOf(() => Create(Data() with { RegionId = MadridRegionId })));

    [Fact]
    public void ForeignSupplierKeepsRegion()
    {
        var supplier = Create(Data(country: "ES", cityName: "Madrid") with { RegionId = MadridRegionId });

        Assert.Equal(MadridRegionId, supplier.RegionId);
    }

    [Fact]
    public void UpdateWithoutRegionClearsIt()
    {
        var supplier = Create(Data(country: "ES", cityName: "Madrid") with { RegionId = MadridRegionId });

        supplier.Update(Data(country: "ES", cityName: "Madrid"), Phones, Later);

        Assert.Null(supplier.RegionId);
    }

    [Fact]
    public void ColombianUpdateWithRegionIsRejectedAndLeavesTheSupplierUntouched()
    {
        var supplier = Create(Data(country: "ES", cityName: "Madrid") with { RegionId = MadridRegionId });

        var code = CodeOf(() => supplier.Update(Data() with { RegionId = MadridRegionId }, Phones, Later));

        Assert.Equal("suppliers.supplier.region_lane_invalid", code);
        Assert.Equal(MadridRegionId, supplier.RegionId);
        Assert.Equal("ES", supplier.Contact.Country);
    }
}
