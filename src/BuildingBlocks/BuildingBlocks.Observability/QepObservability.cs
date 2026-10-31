using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BuildingBlocks.Observability;

public static class QepObservability
{
    public const string ServiceName = "qep-api";
    public const string ActivitySourceName = "Qep.Platform";
    public const string MeterName = "Qep.Platform";
    /// <summary>
    /// Todo meter propio cuelga de este prefijo (<c>Qep.Messaging</c>, <c>Qep.Web</c>…): un meter
    /// nuevo sale por OTLP sin tocar esta lista.
    /// </summary>
    public const string MeterWildcard = "Qep.*";
    public const string NpgsqlMeterName = "Npgsql";

    /// <summary>
    /// Los límites por defecto de OpenTelemetry para <c>http.server.request.duration</c> más el de 2 s.
    /// </summary>
    public static readonly double[] HttpServerDurationBoundaries =
        [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2, 2.5, 5, 7.5, 10];

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <param name="serviceVersion">
    /// El build desplegado (<c>Deployment:BuildNumber</c>, spec 2026-10-14), que es lo que permite
    /// filtrar trazas por despliegue. Sin valor cae a la versión del assembly, que con
    /// <c>Deterministic</c> y sin SourceLink es siempre la misma y no identifica nada.
    /// </param>
    public static IServiceCollection AddQepObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string? serviceVersion = null)
    {
        // OTEL_SERVICE_NAME lo inyecta el Deployment de k8s; la constante es sólo un
        // fallback local/de desarrollo, para no hardcodear el nombre en un entorno real.
        var serviceName = configuration["OTEL_SERVICE_NAME"] ?? ServiceName;
        var endpoint = configuration["OpenTelemetry:Endpoint"];

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceVersion: serviceVersion
                        ?? typeof(QepObservability).Assembly
                            .GetName()
                            .Version?
                            .ToString())
                .AddAttributes(
                [
                    new("deployment.environment", environment.EnvironmentName),
                ]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options => options.RecordException = true)
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
                // OtlpExporterOptions cae a OTEL_EXPORTER_OTLP_ENDPOINT (o a su propio default)
                // cuando no hay endpoint explícito seteado, así que el exportador siempre tiene
                // que registrarse aunque "OpenTelemetry:Endpoint" no esté configurado.
                tracing.AddOtlpExporter(options =>
                {
                    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                    {
                        options.Endpoint = uri;
                    }
                });
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(MeterWildcard)
                    .AddMeter(NpgsqlMeterName)
                    // La meta de «Enviar mensaje» es 2 s y los límites por defecto saltan de 1 a
                    // 2,5: sin un límite en 2 el histograma no distingue 1,5 s de 2,4 s.
                    .AddView(
                        "http.server.request.duration",
                        new ExplicitBucketHistogramConfiguration { Boundaries = HttpServerDurationBoundaries })
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                metrics.AddOtlpExporter(options =>
                {
                    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                    {
                        options.Endpoint = uri;
                    }
                });
            });

        return services;
    }

    /// <summary>
    /// Logging JSON a stdout con TraceId/SpanId incluidos para que Grafana pueda saltar de una
    /// traza a sus logs correlacionados (Loki) sin ningún enricher externo.
    /// </summary>
    public static ILoggingBuilder AddQepLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId
            | ActivityTrackingOptions.SpanId
            | ActivityTrackingOptions.ParentId);
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });
        return logging;
    }
}
