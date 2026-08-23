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
/// </summary>
internal sealed class GeographySeeder(GeographyDbContext dbContext)
{
    private const string DepartmentsResourceSuffix = "Seed.Data.departments.json";
    private const string CitiesResourceSuffix = "Seed.Data.localities.json";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var departmentsByCode = await SeedDepartmentsAsync(cancellationToken);
        await SeedCitiesAsync(departmentsByCode, cancellationToken);
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

    private async Task SeedCitiesAsync(
        Dictionary<string, DepartmentId> departmentsByCode, CancellationToken cancellationToken)
    {
        var records = DivipolaDataParser.ParseCities(OpenResource(CitiesResourceSuffix));

        var existing = await dbContext.Cities
            .ToDictionaryAsync(city => city.DivipolaCode, cancellationToken);

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
            }
        }
    }

    private static Stream OpenResource(string nameSuffix)
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
