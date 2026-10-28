using Modules.Suppliers.Domain;

namespace Modules.Suppliers.UnitTests;

/// <summary>
/// Las cuentas bancarias de un proveedor. Se ejercen a través de <see cref="Supplier.Create"/>,
/// que es quien las normaliza y hace cumplir la colección (el record no expone su normalización).
/// </summary>
public sealed class SupplierBankAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static readonly Guid CityId = Guid.Parse("01a0272d-4c71-722b-abc0-5c2311ae384f");

    private sealed class NoPhones : ISupplierPhoneNormalizer
    {
        public string? ToE164(string? phone, string? country) => null;
    }

    private static SupplierBankAccount Account(
        string bankName = "Bancolombia",
        string accountNumber = "CTA-000123",
        string currency = "COP") =>
        new() { BankName = bankName, AccountNumber = accountNumber, Currency = currency };

    private static Supplier Create(params SupplierBankAccount[] accounts) =>
        Supplier.Create(
            TenantId,
            new SupplierWriteData(
                "Proveedor", null, "NIT", "900", "CO", CityId, null, null, null, null, 0, null, null,
                accounts, []),
            new NoPhones(),
            Now);

    private static string CodeOf(params SupplierBankAccount[] accounts) =>
        Assert.Throws<SuppliersDomainException>(() => Create(accounts)).Code;

    [Fact]
    public void CreateKeepsTheAccountsInTheOrderTheyArrived()
    {
        var supplier = Create(
            Account(bankName: "Bancolombia", accountNumber: "CTA-1"),
            Account(bankName: "Davivienda", accountNumber: "CTA-2", currency: "USD"));

        Assert.Collection(
            supplier.BankAccounts,
            first => Assert.Equal("CTA-1", first.AccountNumber),
            second => Assert.Equal("CTA-2", second.AccountNumber));
    }

    [Fact]
    public void CreateTrimsTheFieldsAndUppercasesTheCurrencyButNotTheNumber()
    {
        var account = Assert.Single(
            Create(Account(bankName: "  Bancolombia ", accountNumber: " cta-1 ", currency: " cop ")).BankAccounts);

        Assert.Equal("Bancolombia", account.BankName);
        Assert.Equal("cta-1", account.AccountNumber);
        Assert.Equal("COP", account.Currency);
    }

    [Theory]
    [InlineData("", "suppliers.supplier.bank_name_required")]
    [InlineData("   ", "suppliers.supplier.bank_name_required")]
    public void CreateWithBlankBankNameThrows(string bank, string code) =>
        Assert.Equal(code, CodeOf(Account(bankName: bank)));

    [Fact]
    public void CreateWithBankNameOf121CharactersThrowsTooLong() =>
        Assert.Equal(
            "suppliers.supplier.bank_name_too_long",
            CodeOf(Account(bankName: new string('b', 121))));

    [Fact]
    public void CreateWithBlankAccountNumberThrows() =>
        Assert.Equal("suppliers.supplier.account_number_required", CodeOf(Account(accountNumber: " ")));

    [Fact]
    public void CreateWithAccountNumberOf33CharactersThrowsTooLong() =>
        Assert.Equal(
            "suppliers.supplier.account_number_too_long",
            CodeOf(Account(accountNumber: new string('1', 33))));

    [Theory]
    [InlineData("")]
    [InlineData("CO")]
    [InlineData("COPP")]
    [InlineData("C0P")]
    public void CreateWithMalformedCurrencyThrows(string currency) =>
        Assert.Equal("suppliers.supplier.currency_invalid", CodeOf(Account(currency: currency)));

    // El número distingue mayúsculas (no se normaliza), el banco no: misma cuenta sólo si el
    // número es idéntico.
    [Fact]
    public void DuplicateDetectionIgnoresBankCaseButNotNumberCase()
    {
        Assert.Equal(
            "suppliers.supplier.bank_account_duplicated",
            CodeOf(Account(bankName: "Bancolombia", accountNumber: " CTA-1"), Account(bankName: "bancolombia", accountNumber: "CTA-1")));

        Assert.Equal(2, Create(Account(accountNumber: "cta-1"), Account(accountNumber: "CTA-1")).BankAccounts.Count);
        Assert.Equal(2, Create(Account(currency: "COP"), Account(currency: "USD")).BankAccounts.Count);
    }
}
