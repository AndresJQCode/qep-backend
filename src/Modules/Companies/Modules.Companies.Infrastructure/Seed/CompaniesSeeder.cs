using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Domain;
using Modules.Companies.Infrastructure.Persistence;

namespace Modules.Companies.Infrastructure.Seed;

/// <summary>
/// La mitad de Companies de la semilla de arranque: las cinco empresas emisoras del tenant con
/// sus cuentas bancarias, tal como las dio el owner el 2026-09-21.
///
/// Construye los agregados con <c>Company.Create</c>, igual que <c>CatalogSeeder</c>, asi que
/// todos los invariantes del dominio siguen valiendo —al menos una cuenta, sin cuentas repetidas,
/// anchos de columna— y lo unico que se saltea respecto de un POST es la capa HTTP.
///
/// Los datos van en codigo y no en un JSON embebido como los de catalogo: son cinco filas fijas
/// que nadie va a editar sin tocar tambien esta clase, y un recurso embebido para eso solo suma
/// el parser, el csproj y la prueba de deserializacion sin comprar nada.
///
/// Idempotente **por NIT**, que es la unica identificacion de negocio que tiene una empresa: no
/// hay codigo como el de producto. Solo crea: una empresa que ya existe no se toca, ni para
/// completarle cuentas que le falten. Ojo con el borde que el modulo documenta en
/// <c>Company.TaxId</c> — el NIT no es unico por diseño, asi que si alguien carga a mano una
/// sucursal con el mismo NIT, el seeder va a dar esa empresa por sembrada.
/// </summary>
public static class CompaniesSeeder
{
    /// <summary>
    /// El domicilio, identico en las cinco: es la misma sede. Se guarda sin el rotulo
    /// "Dirección empresa:" con el que venia la tabla —es la etiqueta del campo, no parte del
    /// dato— y con la ciudad adentro, tal cual lo escribio el owner, aunque <c>CityId</c> ya la
    /// diga: quitarla seria editarle el dato.
    /// </summary>
    private const string SharedAddress =
        "Zona E, centro logístico, bodega 16 del cruce del tablazo 900 mtrs vía zona franca. "
        + "Rionegro, Antioquia";

    private const string SharedPhone = "604 296 6310";

    private const string SavingsBank = "BANCOLOMBIA Ahorros";

    /// <summary>
    /// Siembra las empresas que falten sobre el tenant y la ciudad que se le pasan.
    ///
    /// La ciudad llega resuelta desde afuera —<c>QepSeedRunner</c>, que es el composition root—
    /// y no se busca aca: <c>Modules.Companies.Infrastructure</c> no referencia a Geography, por
    /// la misma regla que obliga a <c>ICompanyGeographyLookup</c> a tener su adaptador en
    /// <c>Bootstrapper</c>.
    /// </summary>
    public static async Task SeedCompaniesAsync(
        this IServiceProvider services,
        Guid tenantId,
        Guid cityId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();

        var existingTaxIds = await dbContext.Companies
            .Where(company => company.TenantId == tenantId)
            .Select(company => company.TaxId)
            .ToListAsync(cancellationToken);
        var existing = new HashSet<string>(existingTaxIds, StringComparer.Ordinal);

        var added = false;
        foreach (var company in BuildSeedCompanies(tenantId, cityId, DateTimeOffset.UtcNow))
        {
            if (!existing.Add(company.TaxId))
            {
                continue;
            }

            dbContext.Companies.Add(company);
            added = true;
        }

        if (added)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Los agregados de la semilla, ya construidos por el dominio.
    ///
    /// internal y no private: <c>CompaniesSeedDataTests</c> lo llama directo via
    /// <c>InternalsVisibleTo</c>, para que un dato que viola un invariante —un NIT demasiado
    /// largo, dos cuentas iguales en una empresa— se vea en milisegundos y no recien en el
    /// arranque del ambiente, que es donde el seeder corre de verdad.
    /// </summary>
    internal static IReadOnlyList<Company> BuildSeedCompanies(
        Guid tenantId, Guid cityId, DateTimeOffset occurredAt)
    {
        var contact = new CompanyContactInfo
        {
            Phone = SharedPhone,
            Address = SharedAddress,

            // El correo no estaba en la tabla. Se deja sin dato en vez de inventarle uno: null es
            // lo que CompanyContactInfo normaliza y lo que el resto del modulo lee como ausente.
            Email = null,
        };

        return
        [
            // Armonia Cosmetica venia en dos filas de la tabla, con el mismo NIT y dos cuentas
            // distintas: es una empresa con dos cuentas. La de Panama va en USD por indicacion
            // del owner; la tabla solo traia el SWIFT, que viaja pegado al numero porque
            // CompanyBankAccount no tiene columna propia para el.
            Create(
                tenantId, cityId, occurredAt, contact,
                "Armonía Cosmética",
                "901.851.609-4",
                new CompanyBankAccount
                {
                    BankName = "BANCOLOMBIA Panamá",
                    AccountNumber = "80100033226 SWIFT (COLOPAPAXXX)",
                    Currency = "USD",
                },
                Savings("00800007542")),
            Create(
                tenantId, cityId, occurredAt, contact,
                "Hechizo de Belleza",
                "901.862.895-1",
                Savings("00800007490")),
            Create(
                tenantId, cityId, occurredAt, contact,
                "Ritual Botánico",
                "901.593.212-7",
                Savings("008000007366")),

            // Sin puntos, tal como lo escribio el owner. No se empareja con el formato de los
            // otros cuatro: el modulo guarda el NIT como viene —Company.NormalizeTaxId solo
            // recorta— y elegir una de las dos formas seria decidir por el.
            Create(
                tenantId, cityId, occurredAt, contact,
                "Grupo Human",
                "901591549-4",
                Savings("00800007620")),
            Create(
                tenantId, cityId, occurredAt, contact,
                "Raíces Orgánicas",
                "901.846.471-5",
                Savings("01400003212")),
        ];
    }

    private static CompanyBankAccount Savings(string accountNumber) => new()
    {
        BankName = SavingsBank,
        AccountNumber = accountNumber,
        Currency = "COP",
    };

    private static Company Create(
        Guid tenantId,
        Guid cityId,
        DateTimeOffset occurredAt,
        CompanyContactInfo contact,
        string name,
        string taxId,
        params CompanyBankAccount[] bankAccounts) =>
        Company.Create(
            CompanyId.New(), tenantId, name, bankAccounts, taxId, cityId, contact, occurredAt);
}
