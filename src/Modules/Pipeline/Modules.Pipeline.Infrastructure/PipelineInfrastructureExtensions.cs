using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Pipeline.Infrastructure;

public static class PipelineInfrastructureExtensions
{
    /// <summary>Se llena en la Task 6 (DbContext, repositorios, unidad de trabajo) y la Task 11 (worker).</summary>
    public static IServiceCollection AddPipelineInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
        services;
}
