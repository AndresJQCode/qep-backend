using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Infrastructure.Persistence;

namespace Modules.Automations.Infrastructure;

public static class AutomationsDatabaseInitializer
{
    public static async Task InitializeAutomationsDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // D-C8: las plantillas del sistema se cargan al arrancar. Resolver el singleton acá lee y valida los JSON embebidos antes de que
        // el host atienda: un archivo roto o ausente tumba el arranque, no el primer GET /templates.
        services.GetRequiredService<ISystemTemplateCatalog>();

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AutomationsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
