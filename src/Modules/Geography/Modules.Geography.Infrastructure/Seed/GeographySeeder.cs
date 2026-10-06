using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;

namespace Modules.Geography.Infrastructure.Seed;

/// <summary>
/// Importador idempotente de DIVIPOLA: corre en cada arranque de la app (llamado desde
/// <see cref="GeographyDatabaseInitializer.InitializeGeographyDatabaseAsync"/>, después de
/// aplicar las migraciones) y hace upsert por <c>DivipolaCode</c> contra los dos JSON embebidos.
/// No es <c>HasData</c> de migración a propósito: DIVIPOLA cambia de año a año — el archivo fuente
/// ya se llama "2026" — así que esto es un importador de datos de referencia, no un fixture fijo.
///
/// Después de las ciudades fija <see cref="City.CoordinadoraName"/> desde un tercer JSON, el
/// snapshot de Coordinadora: el nombre de cada municipio como lo escribe la transportadora, que el
/// Excel de pedidos necesita y el DANE no da. Es un snapshot embebido y no una consulta en vivo a
/// <c>ws.coordinadora.com</c> porque el arranque no puede depender de que un tercero responda; se
/// regenera a mano cuando Coordinadora cambie su lista (README § Nombres de ciudad de
/// Coordinadora). Mismo criterio que las ciudades: cada arranque reconcilia todo contra el archivo.
///
/// Por último aplica los nombres comunes de un cuarto JSON (common-names.json): "CALI" como
/// <see cref="City.Name"/> en vez de "SANTIAGO DE CALI", que queda en
/// <see cref="City.DivipolaName"/>. Va después de las ciudades porque <see cref="City.Rename"/> fija
/// el nombre DANE, y se aplica a todas —las que no están en el archivo vuelven al nombre DANE—,
/// así que sacar una ciudad del archivo le devuelve su nombre oficial en el siguiente arranque.
/// </summary>
internal sealed class GeographySeeder(GeographyDbContext dbContext)
{
    internal const string DepartmentsResourceSuffix = "Seed.Data.departments.json";
    internal const string CitiesResourceSuffix = "Seed.Data.localities.json";
    internal const string CoordinadoraCitiesResourceSuffix = "Seed.Data.coordinadora-cities.json";
    internal const string CommonNamesResourceSuffix = "Seed.Data.common-names.json";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var departmentsByCode = await SeedDepartmentsAsync(cancellationToken);
        var citiesByCode = await SeedCitiesAsync(departmentsByCode, cancellationToken);
        SeedCoordinadoraNames(citiesByCode);
        SeedCommonNames(citiesByCode);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, DepartmentId>> SeedDepartmentsAsync(
        CancellationToken cancellationToken)
    {
        var records = DivipolaDataParser.ParseDepartments(OpenResource(DepartmentsResourceSuffix));

        var existing = await dbContext.Departments
            .ToDictionaryAsync(department => department.DivipolaCode, cancellationToken);

        var departmentsByCode = new Dictionary<string, DepartmentId>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (existing.TryGetValue(record.Code, out var department))
            {
                department.Rename(record.Name);
                departmentsByCode[record.Code] = department.Id;
            }
            else
            {
                var created = Department.Create(DepartmentId.New(), record.Code, record.Name);
                dbContext.Departments.Add(created);
                departmentsByCode[record.Code] = created.Id;
            }
        }

        return departmentsByCode;
    }

    // Devuelve todas las ciudades del archivo por código, las que ya estaban y las creadas en este
    // arranque, para que el paso de Coordinadora alcance también a las recién creadas.
    private async Task<Dictionary<string, City>> SeedCitiesAsync(
        Dictionary<string, DepartmentId> departmentsByCode, CancellationToken cancellationToken)
    {
        var records = DivipolaDataParser.ParseCities(OpenResource(CitiesResourceSuffix));

        var existing = await dbContext.Cities
            .ToDictionaryAsync(city => city.DivipolaCode, cancellationToken);

        var citiesByCode = new Dictionary<string, City>(existing, StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!departmentsByCode.TryGetValue(record.DepartmentCode, out var departmentId))
            {
                throw new InvalidOperationException(
                    $"City '{record.DivipolaCode}' references department code " +
                    $"'{record.DepartmentCode}', which was not found among the seeded " +
                    "departments.");
            }

            if (existing.TryGetValue(record.DivipolaCode, out var city))
            {
                city.Rename(record.Name);
            }
            else
            {
                var created = City.Create(
                    CityId.New(), record.DivipolaCode, record.Name, departmentId);
                dbContext.Cities.Add(created);
                citiesByCode[record.DivipolaCode] = created;
            }
        }

        return citiesByCode;
    }

    // Toda ciudad toma el nombre del snapshot, o null si el snapshot no la trae: así una ciudad que
    // Coordinadora deja de listar pierde el nombre en el siguiente arranque. Un código del snapshot
    // que no sea de ninguna ciudad se ignora en vez de tumbar el arranque; lo que impide que eso
    // llegue a producción es la prueba unitaria que cruza el snapshot con localities.json.
    private static void SeedCoordinadoraNames(Dictionary<string, City> citiesByCode)
    {
        var namesByCode = DivipolaDataParser
            .ParseCoordinadoraNames(OpenResource(CoordinadoraCitiesResourceSuffix))
            .ToDictionary(record => record.DivipolaCode, record => record.Name, StringComparer.Ordinal);

        foreach (var (code, city) in citiesByCode)
        {
            city.SetCoordinadoraName(namesByCode.GetValueOrDefault(code));
        }
    }

    // Toda ciudad toma su nombre común del archivo, o vuelve al nombre DANE si el archivo no la
    // trae. Un código que no sea de ninguna ciudad se ignora, igual que en Coordinadora; lo frena la
    // prueba unitaria que cruza common-names.json con localities.json.
    private static void SeedCommonNames(Dictionary<string, City> citiesByCode)
    {
        var namesByCode = DivipolaDataParser
            .ParseCommonNames(OpenResource(CommonNamesResourceSuffix))
            .ToDictionary(record => record.DivipolaCode, record => record.Name, StringComparer.Ordinal);

        foreach (var (code, city) in citiesByCode)
        {
            city.SetCommonName(namesByCode.GetValueOrDefault(code));
        }
    }

    // Internal y no private: la prueba unitaria que cruza el snapshot de Coordinadora contra
    // localities.json abre los mismos recursos embebidos que el arranque, no una copia en disco.
    internal static Stream OpenResource(string nameSuffix)
    {
        var assembly = typeof(GeographySeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(nameSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded DIVIPOLA resource ending with '{nameSuffix}' was not found in " +
                $"assembly '{assembly.FullName}'.");

        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded DIVIPOLA resource '{resourceName}' could not be opened.");
    }
}
