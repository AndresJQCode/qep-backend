using System.Diagnostics.Metrics;
using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ArchitectureTests;

/// <summary>
/// En producción el destino del exportador OTLP lo fija <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, que
/// pone <c>k8s/prod-configMap.yaml</c>. <c>OpenTelemetry:Endpoint</c> es sólo un override
/// explícito y opcional: cuando tiene valor, <see cref="QepObservability"/> lo asigna a
/// <c>options.Endpoint</c> y eso le gana a la variable estándar.
///
/// El 2026-09-14 no llegó ni una traza a Tempo ni una métrica a Prometheus porque
/// <c>src/Api/appsettings.json</c> —que se carga en todos los ambientes, producción incluida—
/// traía <c>OpenTelemetry:Endpoint = http://localhost:4317</c>. El pod le exportaba a sí mismo,
/// sin error ni log. Esta prueba arma la configuración que de verdad carga producción y verifica
/// a dónde queda apuntando el exportador, no qué dice un archivo.
/// </summary>
public sealed class ObservabilityConfigurationTests
{
    private const string BaseSettingsRelativePath = "src/Api/appsettings.json";
    private const string CollectorEndpoint = "http://otel-collector.example:4317";

    [Fact]
    public void ProductionConfigurationExportsToOtelExporterOtlpEndpoint()
    {
        // Mismo orden que WebApplication.CreateBuilder en Production: appsettings.json, sin
        // overlay de ambiente (no existe appsettings.Production.json), y después las variables de
        // entorno. La variable va en memoria y no en el proceso para no contaminar otras pruebas:
        // el SDK de OpenTelemetry lee las claves OTEL_* del IConfiguration registrado, así que
        // para él es lo mismo.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(BaseSettingsPath(), optional: false, reloadOnChange: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = CollectorEndpoint,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddQepObservability(configuration, new ProductionEnvironment());
        var exporters = RecordExporterOptions(services);
        using var provider = services.BuildServiceProvider();

        // Resolver los providers es lo que construye el procesador de trazas y el lector de
        // métricas, y con ellos las opciones de cada exportador.
        _ = provider.GetRequiredService<TracerProvider>();
        _ = provider.GetRequiredService<MeterProvider>();

        Assert.Equal(2, exporters.Count);
        Assert.All(exporters, options => Assert.Equal(new Uri(CollectorEndpoint), options.Endpoint));
    }

    // El build desplegado va al recurso como service.version (spec 2026-10-14, decisión 10): es lo
    // que permite filtrar trazas por despliegue en Tempo y Grafana. Sin él sigue la versión del
    // assembly, que con Deterministic y sin SourceLink es siempre la misma.
    [Fact]
    public void TheDeploymentBuildNumberBecomesTheServiceVersion()
    {
        Assert.Equal("20261014.3", ServiceVersionWith("20261014.3"));
    }

    [Fact]
    public void WithoutADeploymentBuildTheAssemblyVersionRemains()
    {
        Assert.Equal(
            typeof(QepObservability).Assembly.GetName().Version?.ToString(),
            ServiceVersionWith(null));
    }

    // Todo meter bajo el prefijo Qep. sale por OTLP sin registrarlo uno por uno: los de Messaging y
    // Web los declara cada módulo, y uno que se olvide de la lista sería una métrica que nunca llega.
    [Fact]
    public void AMeterUnderTheQepPrefixIsExported()
    {
        var exporter = new RecordingMetricExporter();
        using var provider = BuildProviderWith(exporter);
        using var meter = new Meter("Qep.Example");
        var counter = meter.CreateCounter<long>("qep.example.count");

        counter.Add(1);
        provider.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Contains("qep.example.count", exporter.MetricNames);
        Assert.Contains("Qep.Example", exporter.MeterNames);
    }

    // La meta de «Enviar mensaje» es 2 s y los límites por defecto saltan de 1 a 2,5: sin un límite
    // en 2 el histograma no distingue 1,5 s (dentro de la meta) de 2,4 s (fuera).
    [Fact]
    public void HttpServerDurationHasTheTwoSecondBucket()
    {
        var exporter = new RecordingMetricExporter();
        using var provider = BuildProviderWith(exporter);
        using var meter = new Meter("Microsoft.AspNetCore.Hosting");
        var histogram = meter.CreateHistogram<double>("http.server.request.duration", unit: "s");

        histogram.Record(1.5);
        provider.GetRequiredService<MeterProvider>().ForceFlush();

        Assert.Equal(
            [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2, 2.5, 5, 7.5, 10],
            exporter.BoundariesOf("http.server.request.duration"));
    }

    private static ServiceProvider BuildProviderWith(RecordingMetricExporter exporter)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = CollectorEndpoint,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddQepObservability(configuration, new ProductionEnvironment());
        services.ConfigureOpenTelemetryMeterProvider(
            builder => builder.AddReader(new BaseExportingMetricReader(exporter)));

        var provider = services.BuildServiceProvider();
        // El MeterProvider se construye al resolverlo, y un valor registrado antes de eso se pierde.
        _ = provider.GetRequiredService<MeterProvider>();
        return provider;
    }

    private static object? ServiceVersionWith(string? serviceVersion)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = CollectorEndpoint,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddQepObservability(configuration, new ProductionEnvironment(), serviceVersion);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<TracerProvider>()
            .GetResource()
            .Attributes
            .Single(attribute => attribute.Key == "service.version")
            .Value;
    }

    // Leer IOptionsMonitor<OtlpExporterOptions> no sirve: da verde aunque el override pise la
    // variable. Con AddOtlpExporter sin nombre, el SDK (1.16) crea una instancia nueva con
    // IOptionsFactory.Create y le aplica el delegate de QepObservability ahí mismo, sin
    // registrarlo en el pipeline de opciones. Por eso se envuelve esa fábrica: las instancias que
    // guarda son las mismas que reciben los exportadores, ya con el delegate aplicado.
    private static List<OtlpExporterOptions> RecordExporterOptions(ServiceCollection services)
    {
        var registration = services.Last(
            descriptor => descriptor.ServiceType == typeof(IOptionsFactory<OtlpExporterOptions>));
        services.Remove(registration);

        var recorded = new List<OtlpExporterOptions>();
        services.AddSingleton<IOptionsFactory<OtlpExporterOptions>>(serviceProvider =>
            new RecordingOptionsFactory(Resolve(registration, serviceProvider), recorded));

        return recorded;
    }

    private static IOptionsFactory<OtlpExporterOptions> Resolve(
        ServiceDescriptor registration,
        IServiceProvider serviceProvider) =>
        (IOptionsFactory<OtlpExporterOptions>)(registration.ImplementationInstance
            ?? registration.ImplementationFactory?.Invoke(serviceProvider)
            ?? ActivatorUtilities.CreateInstance(serviceProvider, registration.ImplementationType!));

    // Se busca hacia arriba desde el directorio de salida, igual que ConfigurationExampleTests:
    // la prueba no puede depender de cuántos niveles hay entre bin/ y la raíz del repositorio.
    private static string BaseSettingsPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, BaseSettingsRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"No se encontró {BaseSettingsRelativePath} subiendo desde {AppContext.BaseDirectory}.");
    }

    private sealed class RecordingOptionsFactory(
        IOptionsFactory<OtlpExporterOptions> inner,
        List<OtlpExporterOptions> recorded) : IOptionsFactory<OtlpExporterOptions>
    {
        public OtlpExporterOptions Create(string name)
        {
            var options = inner.Create(name);
            recorded.Add(options);
            return options;
        }
    }

    // El SDK reutiliza los objetos Metric entre exportaciones, así que se copia lo que importa
    // dentro de Export en vez de guardar la referencia.
    private sealed class RecordingMetricExporter : BaseExporter<Metric>
    {
        private readonly Dictionary<string, double[]> boundaries = [];

        public List<string> MetricNames { get; } = [];

        public List<string> MeterNames { get; } = [];

        public double[] BoundariesOf(string metricName) => boundaries[metricName];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                MetricNames.Add(metric.Name);
                MeterNames.Add(metric.MeterName);
                foreach (var point in metric.GetMetricPoints())
                {
                    if (metric.MetricType != MetricType.Histogram)
                    {
                        continue;
                    }

                    var bounds = new List<double>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        // El último cubo es el de +infinito: no es un límite configurado.
                        if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                        {
                            bounds.Add(bucket.ExplicitBound);
                        }
                    }

                    boundaries[metric.Name] = [.. bounds];
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
