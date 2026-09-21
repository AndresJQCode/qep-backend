using Modules.Companies.Domain;
using Modules.Companies.Infrastructure.Seed;

namespace Modules.Companies.UnitTests;

// Contra los datos reales del seeder, no contra un fixture de prueba: lo que se verifica es que
// las empresas que se van a sembrar construyen agregados validos. Corre en milisegundos, asi que
// un NIT demasiado largo o una cuenta duplicada se detecta sin levantar PostgreSQL — y no recien
// en el arranque del ambiente, que es donde el seeder corre de verdad.
public sealed class CompaniesSeedDataTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000003");

    private static readonly Guid CityId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    private static readonly DateTimeOffset OccurredAt =
        new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    // En el orden de la tabla del owner. Los nombres con tilde se afirman enteros: si el archivo
    // se guarda con la codificacion equivocada, esta es la asercion que lo detecta.
    private static readonly string[] ExpectedNames =
    [
        "Armonía Cosmética",
        "Hechizo de Belleza",
        "Ritual Botánico",
        "Grupo Human",
        "Raíces Orgánicas",
    ];

    [Fact]
    public void BuildsTheFiveCompaniesWithTheirSixBankAccounts()
    {
        var companies = CompaniesSeeder.BuildSeedCompanies(TenantId, CityId, OccurredAt);

        Assert.Equal(5, companies.Count);
        Assert.Equal(6, companies.Sum(company => company.BankAccounts.Count));
        Assert.Equal(5, companies.Select(company => company.TaxId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(companies, company => Assert.Equal(TenantId, company.TenantId));
        Assert.All(companies, company => Assert.Equal(CityId, company.CityId));
        Assert.All(companies, company => Assert.True(company.IsActive));
        // El correo no esta en la tabla que dio el owner. Null y no cadena vacia: es lo que
        // CompanyContactInfo normaliza, y lo que el resto del modulo lee como "no hay dato".
        Assert.All(companies, company => Assert.Null(company.Email));
        Assert.All(companies, company => Assert.Equal("604 296 6310", company.Phone));
        Assert.All(
            companies,
            company => Assert.Equal(
                "Zona E, centro logístico, bodega 16 del cruce del tablazo 900 mtrs vía zona "
                + "franca. Rionegro, Antioquia",
                company.Address));

        Assert.Equal(ExpectedNames, companies.Select(company => company.Name).ToArray());
    }

    // Armonia Cosmetica aparece dos veces en la tabla del owner, con el mismo NIT y dos cuentas
    // distintas: es **una** empresa con dos cuentas, no dos empresas. Si alguien "arregla" el dato
    // partiendola en dos filas, esta prueba es la que se pone roja.
    [Fact]
    public void ArmoniaCosmeticaCarriesBothAccountsWithTheirOwnCurrencies()
    {
        var companies = CompaniesSeeder.BuildSeedCompanies(TenantId, CityId, OccurredAt);

        var armonia = companies.Single(company => company.TaxId == "901.851.609-4");
        Assert.Equal("Armonía Cosmética", armonia.Name);
        Assert.Equal(
            new[]
            {
                new CompanyBankAccount
                {
                    BankName = "BANCOLOMBIA Panamá",
                    AccountNumber = "80100033226 SWIFT (COLOPAPAXXX)",
                    Currency = "USD",
                },
                new CompanyBankAccount
                {
                    BankName = "BANCOLOMBIA Ahorros",
                    AccountNumber = "00800007542",
                    Currency = "COP",
                },
            },
            armonia.BankAccounts.ToArray());
    }

    [Fact]
    public void EveryOtherCompanyHasItsSingleSavingsAccountInPesos()
    {
        var companies = CompaniesSeeder.BuildSeedCompanies(TenantId, CityId, OccurredAt);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["901.862.895-1"] = "00800007490",
            ["901.593.212-7"] = "008000007366",
            // Con puntos como los otros cuatro. La tabla del owner lo traia sin ellos
            // (901591549-4) y lo homologo el 2026-09-21: el formato es de el, no del modulo
            // —Company.NormalizeTaxId solo recorta— asi que hizo falta que lo decidiera.
            ["901.591.549-4"] = "00800007620",
            ["901.846.471-5"] = "01400003212",
        };

        foreach (var (taxId, accountNumber) in expected)
        {
            var account = Assert.Single(
                companies.Single(company => company.TaxId == taxId).BankAccounts);
            Assert.Equal("BANCOLOMBIA Ahorros", account.BankName);
            Assert.Equal(accountNumber, account.AccountNumber);
            Assert.Equal("COP", account.Currency);
        }
    }
}
