using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Ai.Application;
using Modules.Ai.Infrastructure.Persistence;

namespace Modules.Ai.Infrastructure;

public static class AiDatabaseInitializer
{
    public static async Task InitializeAiDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // Spec 2026-11-23: las plantillas de agente se cargan al arrancar. Resolver el singleton acá lee y valida los JSON embebidos antes de
        // que el host atienda: un archivo roto o ausente tumba el arranque, no el primer GET /agent-templates.
        services.GetRequiredService<ISystemAgentTemplateCatalog>();

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AiDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
