using Modules.Suppliers.Application;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// Las reglas de escritura se ejercen por el validador de Create, que las incluye: lo que se
/// verifica es la clave de cada error (PascalCase, con índice en las listas), que es lo único que
/// el formulario sabe leer.
/// </summary>
public sealed class SupplierWriteRulesTests
{
    private static readonly Guid CityId = Guid.NewGuid();

    private static CreateSupplierCommand Command(
        string country = "CO",
        Guid? cityId = null,
        string? cityName = null,
        int paymentTermDays = 30,
        IReadOnlyList<SupplierBankAccountPayload>? bankAccounts = null,
        IReadOnlyList<SupplierContactPayload>? contacts = null) =>
        new(
            Guid.NewGuid(),
            "Ferretería El Tornillo",
            null,
            "NIT",
            "900123456-7",
            country,
            country == "CO" ? cityId ?? CityId : cityId,
            cityName,
            null,
            null,
            null,
            paymentTermDays,
            null,
            null,
            bankAccounts ?? [],
            contacts ?? []);

    private static IReadOnlyList<string> ErrorKeys(CreateSupplierCommand command) =>
        [.. new CreateSupplierValidator().Validate(command).Errors.Select(error => error.PropertyName)];

    private static SupplierBankAccountPayload Account(string number = "123", string currency = "COP") =>
        new("Bancolombia", number, currency);

    [Fact]
    public void AValidColombianCommandPasses() =>
        Assert.Empty(ErrorKeys(Command()));

    [Fact]
    public void AValidForeignCommandPasses() =>
        Assert.Empty(ErrorKeys(Command(country: "US", cityName: "Miami")));

    [Fact]
    public void MissingCityIdInColombiaFailsOnCityId() =>
        Assert.Contains("CityId", ErrorKeys(Command() with { CityId = null }));

    [Fact]
    public void MissingCityNameOutsideColombiaFailsOnCityName() =>
        Assert.Contains("CityName", ErrorKeys(Command(country: "US")));

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void PaymentTermOutsideZeroTo365Fails(int days) =>
        Assert.Contains("PaymentTermDays", ErrorKeys(Command(paymentTermDays: days)));

    [Fact]
    public void TwentyOneBankAccountsFail()
    {
        var accounts = Enumerable.Range(0, 21).Select(index => Account(index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        Assert.Contains("BankAccounts", ErrorKeys(Command(bankAccounts: accounts)));
    }

    [Fact]
    public void DuplicatedBankAccountFailsOnTheRowAccountNumber()
    {
        // El banco no distingue caja ni espacios de borde, igual que la clave del dominio.
        var accounts = new[] { Account("123"), new SupplierBankAccountPayload(" BANCOLOMBIA ", "123", "cop") };

        Assert.Contains("BankAccounts[1].AccountNumber", ErrorKeys(Command(bankAccounts: accounts)));
    }

    [Fact]
    public void UnsupportedCurrencyFailsOnTheRowCurrency() =>
        Assert.Contains(
            "BankAccounts[0].Currency",
            ErrorKeys(Command(bankAccounts: [Account(currency: "XXX")])));

    [Fact]
    public void ElevenContactsFail()
    {
        var contacts = Enumerable.Range(0, 11)
            .Select(index => new SupplierContactPayload($"Contacto {index}", null, null, null))
            .ToArray();

        Assert.Contains("Contacts", ErrorKeys(Command(contacts: contacts)));
    }

    [Fact]
    public void ContactWithoutNameFailsOnTheRowName() =>
        Assert.Contains(
            "Contacts[0].Name",
            ErrorKeys(Command(contacts: [new SupplierContactPayload("", null, null, null)])));

    [Fact]
    public void ContactWithInvalidEmailFailsOnTheRowEmail() =>
        Assert.Contains(
            "Contacts[0].Email",
            ErrorKeys(Command(contacts: [new SupplierContactPayload("Marta", null, null, "no-es-correo")])));

    [Fact]
    public void UpdateValidatorSharesTheSameRules()
    {
        var update = UpdateSupplierCommand.From(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new SupplierWriteRequest(
                "x", null, "NIT", "1", "CO", null, null, null, null, null, 0, null, null, [], []));

        var keys = new UpdateSupplierValidator().Validate(update).Errors.Select(error => error.PropertyName);

        Assert.Contains("CityId", keys);
    }
}
