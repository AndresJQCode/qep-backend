using Modules.Companies.Domain;

namespace Modules.Companies.UnitTests;

/// <summary>
/// Las cuentas bancarias de una empresa (EMP-08).
///
/// Reemplazan al <c>AccountNumber</c> plano que <c>Company</c> tenia hasta este slice. El cambio
/// no contradice a <c>RF-091</c> —que pide "registrar los datos de la empresa, incluido el numero
/// de cuenta" y nada mas—, sino a la unicidad por tenant que el modulo habia agregado de mas:
/// <c>IX_companies_tenant_account_number</c> nunca salio de un requisito.
///
/// La unicidad que si existe es **dentro de la empresa**: la misma terna (banco, moneda, numero)
/// no se carga dos veces. Es invariante del agregado, se comprueba en memoria y por eso no
/// necesita indice ni traduccion de 23505.
/// </summary>
public sealed class CompanyBankAccountTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid TenantId =
        Guid.Parse("01900000-0000-7000-8000-000000000001");

    private static CompanyBankAccount Account(
        string bankName = "Bancolombia",
        string accountNumber = "CTA-000123",
        string currency = "COP") =>
        new() { BankName = bankName, AccountNumber = accountNumber, Currency = currency };

    private static Company Create(params CompanyBankAccount[] accounts) =>
        Company.Create(
            CompanyId.New(),
            TenantId,
            "Andes Logistica S.A.S.",
            accounts.Length == 0 ? [Account()] : accounts,
            "900.111.222-3",
            CompanyContactInfo.Empty,
            Now);

    [Fact]
    public void CreateKeepsTheAccountsInTheOrderTheyArrived()
    {
        var company = Create(
            Account(bankName: "Bancolombia", accountNumber: "CTA-1"),
            Account(bankName: "Davivienda", accountNumber: "CTA-2", currency: "USD"));

        Assert.Collection(
            company.BankAccounts,
            first => Assert.Equal("CTA-1", first.AccountNumber),
            second => Assert.Equal("CTA-2", second.AccountNumber));
    }

    // Recortar es parte del invariante y no higiene del llamador, por la misma razon que lo era
    // para el numero de cuenta plano: " CTA-1" y "CTA-1" son la misma cuenta para cualquiera que
    // lea la lista, y sin recortar la comprobacion de duplicados los deja pasar como distintos.
    [Fact]
    public void CreateTrimsEveryField()
    {
        var company = Create(Account(
            bankName: "  Bancolombia  ",
            accountNumber: "  CTA-1  ",
            currency: "  cop  "));

        var account = Assert.Single(company.BankAccounts);
        Assert.Equal("Bancolombia", account.BankName);
        Assert.Equal("CTA-1", account.AccountNumber);
        Assert.Equal("COP", account.Currency);
    }

    // A mayusculas por el mismo criterio con el que ProductDetails normaliza la moneda en
    // catalogo: "cop" y "COP" son la misma moneda, y guardar las dos formas obliga a cada
    // consumidor a normalizar de nuevo.
    [Fact]
    public void CreateUppercasesTheCurrency()
    {
        var company = Create(Account(currency: "usd"));

        Assert.Equal("USD", Assert.Single(company.BankAccounts).Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankBankName(string bankName)
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(Account(bankName: bankName)));

        Assert.Equal("companies.company.bank_name_required", exception.Code);
    }

    [Fact]
    public void CreateRejectsABankNameLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(() => Create(Account(
            bankName: new string('b', CompanyBankAccount.BankNameMaxLength + 1))));

        Assert.Equal("companies.company.bank_name_too_long", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsABlankAccountNumber(string accountNumber)
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(Account(accountNumber: accountNumber)));

        Assert.Equal("companies.company.account_number_required", exception.Code);
    }

    [Fact]
    public void CreateRejectsAnAccountNumberLongerThanTheColumn()
    {
        var exception = Assert.Throws<CompaniesDomainException>(() => Create(Account(
            accountNumber: new string('1', CompanyBankAccount.AccountNumberMaxLength + 1))));

        Assert.Equal("companies.company.account_number_too_long", exception.Code);
    }

    // Tres letras, como ISO 4217 y como catalogo ya lo hace. No se valida contra una tabla de
    // monedas: mantenerla al dia es un problema propio, y nada en los requisitos dice cuales
    // acepta el producto.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CO")]
    [InlineData("COPS")]
    [InlineData("C0P")]
    public void CreateRejectsACurrencyThatIsNotThreeLetters(string currency)
    {
        var exception = Assert.Throws<CompaniesDomainException>(
            () => Create(Account(currency: currency)));

        Assert.Equal("companies.company.currency_invalid", exception.Code);
    }

    // El invariante que reemplaza al indice unico. Se compara sobre los valores **ya
    // normalizados**: comparar antes de recortar dejaria pasar " CTA-1" junto a "CTA-1".
    [Fact]
    public void CreateRejectsTheSameAccountTwice()
    {
        var exception = Assert.Throws<CompaniesDomainException>(() => Create(
            Account(bankName: "Bancolombia", accountNumber: "CTA-1", currency: "COP"),
            Account(bankName: " bancolombia ", accountNumber: " CTA-1 ", currency: "cop")));

        Assert.Equal("companies.company.bank_account_duplicated", exception.Code);
    }

    // El mismo numero en dos bancos distintos no es un duplicado: son dos cuentas reales. Y la
    // misma cuenta en dos monedas tampoco — una cuenta multimoneda se registra una vez por
    // moneda. Por eso la clave es la terna y no el numero solo.
    [Fact]
    public void CreateAllowsTheSameNumberInAnotherBankOrCurrency()
    {
        var company = Create(
            Account(bankName: "Bancolombia", accountNumber: "CTA-1", currency: "COP"),
            Account(bankName: "Davivienda", accountNumber: "CTA-1", currency: "COP"),
            Account(bankName: "Bancolombia", accountNumber: "CTA-1", currency: "USD"));

        Assert.Equal(3, company.BankAccounts.Count);
    }

    // Una empresa sin ninguna cuenta seria un dato menos completo del que el modulo ya
    // garantizaba: AccountNumber era NOT NULL. Bajar el minimo a cero es ampliar el alcance, y
    // eso lo decide el gate del modulo, no este slice.
    [Fact]
    public void CreateRejectsAnEmptyAccountList()
    {
        var exception = Assert.Throws<CompaniesDomainException>(() => Company.Create(
            CompanyId.New(),
            TenantId,
            "Andes",
            [],
            "900-1",
            CompanyContactInfo.Empty,
            Now));

        Assert.Equal("companies.company.bank_accounts_required", exception.Code);
    }

    // Tope defensivo. Sin el, un cuerpo con diez mil cuentas se convierte en diez mil INSERT en
    // una sola transaccion; el limite corta eso como 422 y no como timeout.
    [Fact]
    public void CreateRejectsMoreAccountsThanTheLimit()
    {
        var tooMany = Enumerable
            .Range(0, CompanyBankAccount.MaxPerCompany + 1)
            .Select(index => Account(accountNumber: $"CTA-{index}"))
            .ToArray();

        var exception = Assert.Throws<CompaniesDomainException>(() => Create(tooMany));

        Assert.Equal("companies.company.bank_accounts_too_many", exception.Code);
    }

    // El PUT reemplaza el recurso entero, y la coleccion no es la excepcion: lo que no viene en
    // el cuerpo se **quita**. Es la misma regla que ya rige a los tres opcionales de contacto.
    [Fact]
    public void UpdateReplacesTheWholeCollection()
    {
        var company = Create(
            Account(accountNumber: "CTA-1"),
            Account(accountNumber: "CTA-2"));

        company.Update(
            "Andes",
            [Account(bankName: "Davivienda", accountNumber: "CTA-9", currency: "USD")],
            "900-1",
            CompanyContactInfo.Empty,
            Now.AddMinutes(5));

        var account = Assert.Single(company.BankAccounts);
        Assert.Equal("Davivienda", account.BankName);
        Assert.Equal("CTA-9", account.AccountNumber);
        Assert.Equal("USD", account.Currency);
    }

    [Fact]
    public void UpdateRejectsAnEmptyAccountList()
    {
        var company = Create();

        var exception = Assert.Throws<CompaniesDomainException>(() => company.Update(
            "Andes",
            [],
            "900-1",
            CompanyContactInfo.Empty,
            Now.AddMinutes(5)));

        Assert.Equal("companies.company.bank_accounts_required", exception.Code);
    }

    // Que el agregado quede intacto cuando la escritura se rechaza no es un detalle: Update muta
    // en el sitio, asi que validar despues de haber vaciado la coleccion dejaria a la empresa sin
    // cuentas en memoria aunque el 422 diga que no se guardo nada. Con tracking de EF, esa copia
    // rota es la que se persistiria en el siguiente SaveChanges de la misma unidad de trabajo.
    [Fact]
    public void UpdateLeavesTheCompanyUntouchedWhenItIsRejected()
    {
        var company = Create(Account(accountNumber: "CTA-1"));

        Assert.Throws<CompaniesDomainException>(() => company.Update(
            "Andes",
            [Account(accountNumber: "CTA-9"), Account(accountNumber: "CTA-9")],
            "900-1",
            CompanyContactInfo.Empty,
            Now.AddMinutes(5)));

        Assert.Equal("CTA-1", Assert.Single(company.BankAccounts).AccountNumber);
        Assert.Equal(1, company.Version);
    }
}
