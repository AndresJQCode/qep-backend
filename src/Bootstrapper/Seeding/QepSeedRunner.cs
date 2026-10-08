using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Catalog.Infrastructure.Seed;
using Modules.Companies.Infrastructure.Seed;
using Modules.Geography.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Seed;
using Modules.Quotations.Infrastructure.Seed;
using Modules.Tenancy.Infrastructure.Seed;
using Npgsql;

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

    private static readonly Action<ILogger, string, Exception?> LogOperatorOwnerEmailMissing =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4102, nameof(LogOperatorOwnerEmailMissing)),
            "Seed:OperatorOwnerEmail is not configured: the operator tenant '{TenantSlug}' was not "
            + "seeded, so nobody can open the operator console. Set Seed__OperatorOwnerEmail to seed it.");

    private static readonly Action<ILogger, string, Guid, Exception?> LogOperatorSlugTaken =
        LoggerMessage.Define<string, Guid>(
            LogLevel.Warning,
            new EventId(4103, nameof(LogOperatorSlugTaken)),
            "The operator tenant was not seeded: slug '{TenantSlug}' already belongs to tenant "
            + "{TenantId}. Before pointing Platform:OperatorTenantId at it, verify who owns that tenant: "
            + "whoever administers it gets platform-wide operator power.");

    private static readonly Action<ILogger, string, Exception?> LogOperatorOwnerIsTheOwnerEmail =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4104, nameof(LogOperatorOwnerIsTheOwnerEmail)),
            "SECURITY: the operator tenant '{TenantSlug}' was NOT seeded because Seed:OperatorOwnerEmail "
            + "is the same address as Seed:OwnerEmail. The operator owner gets platform-wide power: use an "
            + "internal QCode address that nobody else uses.");

    private static readonly Action<ILogger, string, string, Exception?> LogOperatorOwnerBelongsElsewhere =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(4105, nameof(LogOperatorOwnerBelongsElsewhere)),
            "SECURITY: the operator tenant '{TenantSlug}' was NOT seeded because Seed:OperatorOwnerEmail "
            + "'{OperatorOwnerEmail}' already belongs to a user with a membership in another tenant. The "
            + "operator owner gets platform-wide power: use an internal QCode address that nobody else uses.");

    private static readonly Action<ILogger, string, string?, Exception?> LogOperatorSeedLostRace =
        LoggerMessage.Define<string, string?>(
            LogLevel.Warning,
            new EventId(4106, nameof(LogOperatorSeedLostRace)),
            "The operator tenant '{TenantSlug}' was not seeded by this instance: unique constraint "
            + "'{Constraint}' was violated, most likely because another instance seeded it at the same "
            + "time. Startup continues; the next start finds it already seeded.");

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

        // El usuario va primero porque el tenant nace nombrando a su membresía dueña, y esa
        // membresía necesita a quién pertenece. Sin ella el tenant queda invisible: los permisos
        // se resuelven desde la membresía, así que sin admin activo cualquier request daría 403.
        var ownerUserId = await services.SeedUserAsync(options.OwnerEmail!, cancellationToken);
        await services.SeedTenantWithOwnerAsync(ownerUserId, cancellationToken);

        // Pegado al tenant y antes que todo lo demas: el primer pedido que se convierta ya tiene
        // que salir con la serie del cliente (PW...), no con el PED- del default.
        await services.SeedOrderNumberingAsync(TenancySeeder.SeedTenantId, cancellationToken);

        // Mismo motivo: el primer Excel de pedidos ya sale con la hoja de importación del ERP del
        // tenant (MIGRACION 1) y no con el catálogo por defecto.
        await services.SeedOrdersExportLayoutAsync(TenancySeeder.SeedTenantId, cancellationToken);

        await services.SeedCatalogAsync(TenancySeeder.SeedTenantId, cancellationToken);

        // Despues del catalogo y no antes por comodidad de lectura nada mas: las empresas no
        // dependen de el. La ciudad viaja como resolvedor y no resuelta: el seeder la pide solo
        // si hay empresas que crear, que es el primer arranque y ninguno mas.
        await services.SeedCompaniesAsync(
            TenancySeeder.SeedTenantId,
            token => ResolveCompaniesCityAsync(scope.ServiceProvider, token),
            cancellationToken);

        // Al final y aparte: el tenant operador no depende de nada de lo anterior, y lo que le pase
        // —sin email, slug ocupado— se advierte sin tumbar el arranque ni tocar Origen botánico.
        // Rolling update: dos pods pueden sembrar a la vez, y el que pierde choca contra un índice
        // único (el email del usuario, el id o el slug del tenant). Sólo ese paso y sólo esa falla se
        // atrapan: el otro pod ya lo sembró, y el próximo arranque lo encuentra hecho. Se atrapa acá y
        // no en un seeder porque el paso escribe en dos módulos (Identity y Tenancy) con unidades de
        // trabajo distintas; no es una capa Application, así que no rompe la regla de traducir
        // errores de base fuera de ella.
        try
        {
            await SeedOperatorTenantAsync(
                services, options.OwnerEmail, options.OperatorOwnerEmail, logger, cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            LogOperatorSeedLostRace(logger, TenancySeeder.OperatorTenantSlug, postgres.ConstraintName, null);
        }
    }

    // Sin email se advierte y no se crea: en producción Seed:Enabled corre en cada arranque, y hasta
    // que el owner agregue la clave al ConfigMap esto es el estado normal, no una falla. Con email
    // inválido no se llega acá: lo rechaza SeedOptionsValidator al arrancar.
    private static async Task SeedOperatorTenantAsync(
        IServiceProvider services,
        string? ownerEmail,
        string? operatorOwnerEmail,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operatorOwnerEmail))
        {
            LogOperatorOwnerEmailMissing(logger, TenancySeeder.OperatorTenantSlug, null);
            return;
        }

        // Se mira antes de crear el usuario: con el slug ocupado, el usuario quedaría sin membresía.
        var (state, slugHolderId) = await services.InspectOperatorTenantAsync(cancellationToken);
        switch (state)
        {
            case TenancySeeder.OperatorTenantSeedState.AlreadySeeded:
                return;
            case TenancySeeder.OperatorTenantSeedState.SlugTaken:
                LogOperatorSlugTaken(logger, TenancySeeder.OperatorTenantSlug, slugHolderId!.Value, null);
                return;
            case TenancySeeder.OperatorTenantSeedState.Missing:
                break;
            default:
                throw new InvalidOperationException($"Unknown operator tenant seed state '{state}'.");
        }

        // El dueño del operador recibe poder sobre toda la plataforma, así que un email equivocado no
        // se siembra en silencio. Mismo email que Seed:OwnerEmail —en producción, quizá el del
        // cliente— o un usuario que ya pertenece a otro tenant: se niega y lo advierte, sin tumbar el
        // arranque. Los dos ya pasaron por SeedOptionsValidator, así que normalizan sin fallar.
        var normalizedOperatorEmail = User.NormalizeEmail(operatorOwnerEmail);
        if (!string.IsNullOrWhiteSpace(ownerEmail)
            && User.NormalizeEmail(ownerEmail) == normalizedOperatorEmail)
        {
            LogOperatorOwnerIsTheOwnerEmail(logger, TenancySeeder.OperatorTenantSlug, null);
            return;
        }

        if (await services.FindUserIdByEmailAsync(normalizedOperatorEmail, cancellationToken) is { } existingUserId
            && await services.HasMembershipOutsideOperatorTenantAsync(existingUserId, cancellationToken))
        {
            LogOperatorOwnerBelongsElsewhere(
                logger, TenancySeeder.OperatorTenantSlug, normalizedOperatorEmail, null);
            return;
        }

        LogSeedEnabled(logger, TenancySeeder.OperatorTenantSlug, normalizedOperatorEmail, null);
        var ownerUserId = await services.SeedUserAsync(normalizedOperatorEmail, cancellationToken);
        await services.SeedOperatorTenantWithOwnerAsync(ownerUserId, cancellationToken);
    }

    // Revienta en vez de saltear la siembra: una empresa sin ciudad no se puede crear —CityId es
    // FK a geography.cities y Company.Create la exige—, asi que si el DIVIPOLA no trajo Rionegro
    // el ambiente esta mal y tiene que verse en el arranque, no como una tabla vacia que alguien
    // descubre semanas despues.
    //
    // Solo corre cuando falta al menos una empresa, porque el seeder invoca el resolvedor recien
    // ahi. Un ambiente ya sembrado no vuelve a consultar a Geography ni puede caerse por esto.
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
