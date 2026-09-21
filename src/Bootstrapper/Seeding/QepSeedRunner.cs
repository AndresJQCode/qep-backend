using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Catalog.Infrastructure.Seed;
using Modules.Companies.Infrastructure.Seed;
using Modules.Geography.Application;
using Modules.Identity.Infrastructure.Seed;
using Modules.Tenancy.Infrastructure.Seed;

namespace Bootstrapper.Seeding;

public static class QepSeedRunner
{
    /// <summary>
    /// La sede de las cinco empresas de la semilla: Rionegro, Antioquia — DIVIPOLA 05615, la
    /// fila que <c>GeographySeeder</c> importa desde <c>localities.json</c>.
    ///
    /// Se resuelve por nombre dentro del departamento y no por codigo DIVIPOLA porque es la unica
    /// busqueda que <c>Geography</c> expone —la misma que usa la importacion masiva de clientes—,
    /// y por nombre de ciudad solo nunca: RIONEGRO se repite en cinco departamentos del DIVIPOLA.
    /// </summary>
    private const string CompaniesDepartmentName = "Antioquia";

    private const string CompaniesCityName = "Rionegro";

    private static readonly Action<ILogger, string, string, Exception?> LogSeedEnabled =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4100, nameof(LogSeedEnabled)),
            "Seeding is ENABLED. Creating tenant '{TenantSlug}' and granting the admin role "
            + "to '{OwnerEmail}'. Disable Seed:Enabled before handing this environment over.");

    /// <summary>
    /// Corre la semilla del ambiente desplegado. No hace nada si <c>Seed:Enabled</c> está
    /// apagado. Es idempotente: lo que ya existe se saltea.
    /// </summary>
    public static async Task RunQepSeedAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        // Ruidoso a propósito: si el ambiente pasa al cliente con la clave prendida, tiene que
        // verse en los logs del primer arranque y no seis meses después.
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(QepSeedRunner).FullName!);
        LogSeedEnabled(logger, "origen-botanico", options.OwnerEmail!, null);

        await services.SeedTenantAsync(cancellationToken);

        var ownerUserId = await services.SeedUserAsync(options.OwnerEmail!, cancellationToken);
        // Sin esta membresía el tenant sembrado queda invisible: los permisos se resuelven
        // desde ella, así que sin admin activo cualquier request devolvería 403.
        await services.SeedOwnerMembershipAsync(ownerUserId, cancellationToken);

        await services.SeedCatalogAsync(TenancySeeder.SeedTenantId, cancellationToken);

        // Despues del catalogo y no antes por comodidad de lectura nada mas: las empresas no
        // dependen de el. La ciudad si es un prerrequisito duro, y ya esta: Geography se importa
        // en InitializeGeographyDatabaseAsync, antes de esta semilla (Program.cs).
        var cityId = await ResolveCompaniesCityAsync(scope.ServiceProvider, cancellationToken);
        await services.SeedCompaniesAsync(TenancySeeder.SeedTenantId, cityId, cancellationToken);
    }

    // Revienta en vez de saltear la siembra: una empresa sin ciudad no se puede crear —CityId es
    // FK a geography.cities y Company.Create la exige—, asi que si el DIVIPOLA no trajo Rionegro
    // el ambiente esta mal y tiene que verse en el arranque, no como una tabla vacia que alguien
    // descubre semanas despues.
    private static async Task<Guid> ResolveCompaniesCityAsync(
        IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var department = await scopedServices.GetRequiredService<IDepartmentRepository>()
            .FindByNameAsync(CompaniesDepartmentName, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The companies seed needs the department '{CompaniesDepartmentName}': "
                + "geography.departments has no such row.");

        var city = await scopedServices.GetRequiredService<ICityRepository>()
            .FindByNameAsync(department.Id, CompaniesCityName, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The companies seed needs the city '{CompaniesCityName}' in "
                + $"'{CompaniesDepartmentName}': geography.cities has no such row.");

        return city.Id.Value;
    }
}
