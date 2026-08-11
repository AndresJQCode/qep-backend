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
    public const string NpgsqlMeterName = "Npgsql";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static IServiceCollection AddQepObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // OTEL_SERVICE_NAME lo inyecta el Deployment de k8s; la constante es sólo un
        // fallback local/de desarrollo, para no hardcodear el nombre en un entorno real.
        var serviceName = configuration["OTEL_SERVICE_NAME"] ?? ServiceName;
        var endpoint = configuration["OpenTelemetry:Endpoint"];

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceVersion: typeof(QepObservability).Assembly
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
                    .AddMeter(MeterName)
                    .AddMeter(NpgsqlMeterName)
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
