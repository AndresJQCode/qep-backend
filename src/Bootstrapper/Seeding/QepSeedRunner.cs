using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Infrastructure.Seed;

namespace Bootstrapper.Seeding;

public static class QepSeedRunner
{
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
    }
}
