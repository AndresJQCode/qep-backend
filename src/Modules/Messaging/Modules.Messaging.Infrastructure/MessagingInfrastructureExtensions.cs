using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Messaging.Infrastructure;

public static class MessagingInfrastructureExtensions
{
    /// <summary>Se llena en la Task 10 (DbContext, opciones) y siguientes (workers, clientes).</summary>
    public static IServiceCollection AddMessagingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration) =>
        services;
}
