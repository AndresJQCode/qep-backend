using BuildingBlocks.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
