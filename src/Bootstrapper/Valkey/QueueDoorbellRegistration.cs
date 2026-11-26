using System.Reflection;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bootstrapper.Valkey;

/// <summary>
/// El interceptor de timbres en cada <c>DbContext</c> de módulo (spec 2026-11-13, D-7; plan P10). Se agrega con
/// <c>ConfigureDbContext</c>, que en EF Core 10 se compone con el <c>AddDbContext</c> del módulo, después de que todos los
/// módulos registraron los suyos: un módulo nuevo queda cubierto sin tocar este archivo. Lo amarra
/// <c>CompositionRootTests.EveryDbContextThatMapsADoorbellTableHasTheInterceptor</c>.
/// </summary>
internal static class QueueDoorbellRegistration
{
    private static readonly MethodInfo AddToContext =
        typeof(QueueDoorbellRegistration).GetMethod(nameof(AddInterceptorTo), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static IServiceCollection AddQueueDoorbellInterceptor(this IServiceCollection services)
    {
        services.AddSingleton<QueueDoorbellInterceptor>();
        var contexts = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => typeof(DbContext).IsAssignableFrom(type)
                && type != typeof(DbContext)
                && type.Namespace?.StartsWith("Modules.", StringComparison.Ordinal) == true)
            .Distinct()
            .ToList();
        foreach (var context in contexts)
        {
            AddToContext.MakeGenericMethod(context).Invoke(null, [services]);
        }

        return services;
    }

    private static void AddInterceptorTo<TContext>(IServiceCollection services)
        where TContext : DbContext =>
        services.ConfigureDbContext<TContext>((provider, options) =>
            options.AddInterceptors(provider.GetRequiredService<QueueDoorbellInterceptor>()));
}
