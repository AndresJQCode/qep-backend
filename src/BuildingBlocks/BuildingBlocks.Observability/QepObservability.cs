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
        // OTEL_SERVICE_NAME is injected by the k8s Deployment; the constant is only a
        // local/dev fallback so the service name is never hardcoded for a real environment.
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
                // OtlpExporterOptions falls back to OTEL_EXPORTER_OTLP_ENDPOINT (or its own
                // default) when no explicit endpoint is set, so the exporter must always be
                // registered even if "OpenTelemetry:Endpoint" is not configured.
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
    /// Stdout JSON logging with TraceId/SpanId included so Grafana can jump from a trace to
    /// its correlated logs (Loki) without any external enricher.
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
