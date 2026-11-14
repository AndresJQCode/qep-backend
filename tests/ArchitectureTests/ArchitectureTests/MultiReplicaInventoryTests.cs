using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Bootstrapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ArchitectureTests;

/// <summary>
/// Spec 2026-10-29 (réplicas múltiples), D-6: todo hosted service y todo singleton con estado de nuestros ensamblados
/// dice cómo convive con otro pod, en <see cref="MultiReplicaInventory"/>. Mira el contenedor real
/// (<c>AddQepPlatform</c>), no el código fuente, así que un worker registrado por otra vía también aparece.
///
/// No ve los <c>static</c> mutables ni el estado interno de <c>AddRateLimiter</c> (del framework): los primeros se
/// revisan a mano en el inventario del plan (decisión P4), y los limitadores los comparte el área <c>RateLimiting</c>
/// de Valkey.
/// </summary>
public sealed class MultiReplicaInventoryTests
{
    private static readonly Type[] StatefulGenericDefinitions =
    [
        typeof(ConcurrentDictionary<,>),
        typeof(Dictionary<,>),
        typeof(HashSet<>),
        typeof(List<>),
        typeof(Queue<>),
        typeof(ConcurrentQueue<>),
        typeof(ConcurrentBag<>),
        typeof(Channel<>),

        // Fuera de la lista del plan (P4), a propósito: las particiones de un limitador son estado en memoria, y sin esto
        // ApiKeyRateLimiter, que el inventario del plan sí nombra, no aparecería.
        typeof(PartitionedRateLimiter<>),
    ];

    private static readonly Type[] StatefulTypes = [typeof(SemaphoreSlim), typeof(IMemoryCache), typeof(MemoryCache)];

    private static readonly string[] OurAssemblyPatterns = ["Modules.*.dll", "Bootstrapper.dll", "BuildingBlocks.*.dll"];

    // Las propiedades de ValkeyAreaOptions (src/Bootstrapper/Valkey/ValkeyOptions.cs).
    private static readonly string[] ValkeyAreas =
        ["Presence", "RateLimiting", "Sessions", "Permissions", "SendBudget", "Doorbells", "ApiKeyTouch"];

    [Fact]
    public void EveryHostedServiceIsClassified()
    {
        var classified = MultiReplicaInventory.Workers.Select(entry => entry.TypeFullName).ToHashSet(StringComparer.Ordinal);
        var missing = HostedServices().Where(name => !classified.Contains(name)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "Estos hosted services no dicen cómo conviven con otro pod. Agrégalos a MultiReplicaInventory.Workers con su "
            + "clasificación y evidencia (spec 2026-10-29 D-6): " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryStatefulSingletonIsClassified()
    {
        var classified = MultiReplicaInventory.State.Select(entry => entry.TypeFullName).ToHashSet(StringComparer.Ordinal);
        var missing = StatefulTypesDetected().Where(name => !classified.Contains(name)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "Estos singletons guardan estado en memoria y no dicen cómo conviven con otro pod. Agrégalos a "
            + "MultiReplicaInventory.State con su clasificación y evidencia (spec 2026-10-29 D-6): " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryClassificationStillMatchesARegistration()
    {
        var hosted = HostedServices();
        var stateful = StatefulTypesDetected();
        var staleWorkers = MultiReplicaInventory.Workers.Select(entry => entry.TypeFullName).Where(name => !hosted.Contains(name)).ToList();
        var staleState = MultiReplicaInventory.State.Select(entry => entry.TypeFullName).Where(name => !stateful.Contains(name)).ToList();

        Assert.True(staleWorkers.Count == 0 && staleState.Count == 0,
            "Estas entradas de MultiReplicaInventory ya no corresponden a un hosted service registrado o a un estado "
            + "detectado; quítalas: " + string.Join(", ", staleWorkers.Concat(staleState)));
    }

    [Fact]
    public void EveryEntryCitesItsEvidence()
    {
        Assert.All(MultiReplicaInventory.Workers, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Evidence), entry.TypeFullName));
        Assert.All(MultiReplicaInventory.State, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Evidence), entry.TypeFullName));
        Assert.DoesNotContain(MultiReplicaInventory.Workers.GroupBy(entry => entry.TypeFullName, StringComparer.Ordinal), group => group.Count() > 1);
        Assert.DoesNotContain(MultiReplicaInventory.State.GroupBy(entry => entry.TypeFullName, StringComparer.Ordinal), group => group.Count() > 1);
    }

    [Fact]
    public void MoreThanOneReplicaRequiresNoUnsafeEntry()
    {
        var deployment = File.ReadAllText(RepositoryFile("k8s/prod-deployment.yaml"));
        var match = Regex.Match(deployment, @"(?m)^\s{2}replicas:\s*(\d+)\s*$");
        Assert.True(match.Success, "No se encontró `replicas:` en k8s/prod-deployment.yaml.");
        var replicas = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        if (replicas > 1)
        {
            Assert.DoesNotContain(MultiReplicaInventory.Workers, entry => entry.Classification == WorkerClassification.Unsafe);
            Assert.DoesNotContain(MultiReplicaInventory.State, entry => entry.Classification == StateClassification.Unsafe);

            // OffInProduction: con dos pods, ExportLoadSeedWorker sembraría dos veces; sólo vale apagado.
            Assert.Matches(@"(?m)^\s*Seed__ExportLoad__Quotations:\s*""0""\s*$", File.ReadAllText(RepositoryFile("k8s/prod-configMap.yaml")));
        }
    }

    /// <summary>
    /// Los hosted services de nuestros ensamblados, con todas las áreas opcionales prendidas: un worker que sólo se registra
    /// con un interruptor (el latido de presencia de Valkey) también tiene que estar clasificado.
    /// </summary>
    private static HashSet<string> HostedServices()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in AllRegistrations().Where(descriptor => descriptor.ServiceType == typeof(IHostedService)))
        {
            var type = ImplementationTypeOf(descriptor);
            Assert.True(type is not null && type != typeof(IHostedService) && type != typeof(object),
                "Un hosted service está registrado con una fábrica sin tipo (" + descriptor.ImplementationFactory?.Method.DeclaringType
                + "): usa AddHostedService<TuWorker>(sp => …) para que el inventario D-6 sepa qué es.");
            if (IsOurs(type))
            {
                names.Add(type.FullName!);
            }
        }

        return names;
    }

    /// <summary>
    /// Singletons nuestros que declaran un campo de instancia mutable (no <c>readonly</c>, sin contar los generados por el
    /// compilador) o un campo de un tipo con estado (colecciones, canales, semáforos, cachés), con cualquier nombre: los
    /// parámetros capturados de un constructor primario son campos <c>&lt;x&gt;P</c>. Más todo tipo nuestro que pida
    /// <see cref="IMemoryCache"/> en el constructor, con cualquier vida. Los hosted services se dejan fuera: ya los
    /// clasifica <see cref="MultiReplicaInventory.Workers"/>.
    /// </summary>
    private static HashSet<string> StatefulTypesDetected()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in AllRegistrations().Where(descriptor =>
                     descriptor.Lifetime == ServiceLifetime.Singleton && descriptor.ServiceType != typeof(IHostedService)))
        {
            var type = ImplementationTypeOf(descriptor);
            if (type is not null && IsOurs(type) && HasState(type))
            {
                names.Add(type.FullName!);
            }
        }

        foreach (var type in OurAssemblies().SelectMany(LoadableTypes))
        {
            if (type is { IsAbstract: false, IsInterface: false } && type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IMemoryCache))))
            {
                names.Add(type.FullName!);
            }
        }

        return names;
    }

    private static bool HasState(Type type)
    {
        for (var current = type; current is not null && IsOurs(current); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if ((!field.IsInitOnly && !field.Name.StartsWith('<')) || IsStatefulFieldType(field.FieldType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsStatefulFieldType(Type fieldType) =>
        StatefulTypes.Contains(fieldType)
        || (fieldType.IsGenericType && StatefulGenericDefinitions.Contains(fieldType.GetGenericTypeDefinition()));

    /// <summary>
    /// El tipo concreto de un registro. Para una fábrica (<c>AddHostedService(sp =&gt; new X(…))</c>,
    /// <c>ImplementationType</c> es <c>null</c>) sale del segundo argumento de <c>Func&lt;IServiceProvider, T&gt;</c>.
    /// </summary>
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService)
        {
            return descriptor.KeyedImplementationType
                ?? descriptor.KeyedImplementationInstance?.GetType()
                ?? FactoryReturnType(descriptor.KeyedImplementationFactory);
        }

        return descriptor.ImplementationType
            ?? descriptor.ImplementationInstance?.GetType()
            ?? FactoryReturnType(descriptor.ImplementationFactory);
    }

    private static Type? FactoryReturnType(Delegate? factory) => factory?.GetType().GenericTypeArguments.LastOrDefault();

    private static bool IsOurs(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        return assembly.StartsWith("Modules.", StringComparison.Ordinal)
            || assembly.StartsWith("Bootstrapper", StringComparison.Ordinal)
            || assembly.StartsWith("BuildingBlocks", StringComparison.Ordinal);
    }

    private static IEnumerable<Assembly> OurAssemblies() =>
        OurAssemblyPatterns
            .SelectMany(pattern => Directory.GetFiles(AppContext.BaseDirectory, pattern))
            .Select(Assembly.LoadFrom);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    /// <summary>
    /// Los registros con la configuración mínima de <c>CompositionRootTests</c> más los de todas las áreas de Valkey
    /// prendidas: lo que un interruptor agrega también tiene que estar clasificado.
    /// </summary>
    private static List<ServiceDescriptor> AllRegistrations()
    {
        var minimal = new ServiceCollection();
        minimal.AddQepPlatform(Configuration(allAreasOn: false), new TestHostEnvironment());
        var allOn = new ServiceCollection();
        allOn.AddQepPlatform(Configuration(allAreasOn: true), new TestHostEnvironment());
        return [.. minimal, .. allOn];
    }

    // Valores de juguete, como en CompositionRootTests: nada se construye ni se conecta, sólo se mira qué quedó registrado.
    private static IConfiguration Configuration(bool allAreasOn)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:QepDatabase"] = "Host=localhost;Port=5432;Database=architecture_tests;Username=x;Password=x",
            ["Storage:R2:AccountId"] = "account",
            ["Storage:R2:AccessKeyId"] = "key",
            ["Storage:R2:SecretAccessKey"] = "secret",
            ["Storage:R2:Bucket"] = "bucket",
            ["Notifications:EmailProvider"] = "log",
        };
        if (allAreasOn)
        {
            values["Valkey:Enabled"] = "true";
            foreach (var area in ValkeyAreas)
            {
                values[$"Valkey:Areas:{area}"] = "true";
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string RepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"No se encontró {relativePath} subiendo desde {AppContext.BaseDirectory}.");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ArchitectureTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
