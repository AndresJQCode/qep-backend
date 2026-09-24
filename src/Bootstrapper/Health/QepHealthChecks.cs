using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bootstrapper.Health;

/// <summary>
/// La readiness del pod (plan 2026-09-24, tarea 1). Vive acá y no en <c>Api</c> porque el
/// chequeo abre una <c>NpgsqlConnection</c>, y el Bootstrapper ya es quien habla con Npgsql
/// fuera de los módulos (ver <c>ExportLoadSeeder</c>).
/// </summary>
public static class QepHealthChecks
{
    /// <summary>La etiqueta que filtra lo que corre <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    // Por debajo del timeoutSeconds: 3 del readinessProbe (k8s/prod-deployment.yaml): si la base
    // no contesta, el endpoint alcanza a responder 503 antes de que el kubelet corte la espera.
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(2);

    public static IServiceCollection AddQepHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");

        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                "database",
                _ => new DatabaseReadinessCheck(connectionString),
                HealthStatus.Unhealthy,
                [ReadyTag],
                DatabaseTimeout));
        return services;
    }

    /// <summary>
    /// <c>GET /health/ready</c>: 200 si la base responde, 503 si no. Anónimo, como
    /// <c>/health/live</c>: el kubelet no manda credenciales.
    /// </summary>
    public static IEndpointConventionBuilder MapQepReadiness(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHealthChecks(
                "/health/ready",
                new HealthCheckOptions
                {
                    Predicate = registration => registration.Tags.Contains(ReadyTag),
                })
            .AllowAnonymous();
}
