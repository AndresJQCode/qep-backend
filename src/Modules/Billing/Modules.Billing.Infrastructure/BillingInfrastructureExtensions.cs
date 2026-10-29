using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Billing.Infrastructure;

public static class BillingInfrastructureExtensions
{
    // Vacío a propósito en esta tarea: el DbContext, los repositorios y el worker se registran en
    // las tareas siguientes. Existe ya para que el composition root llame a un solo punto de entrada.
    public static IServiceCollection AddBillingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration) => services;
}
