using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Bootstrapper;
using Bootstrapper.Cors;
using Bootstrapper.Health;
using Bootstrapper.ReverseProxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Messaging.Api.Realtime;

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

    private static readonly string[] OurAssemblyPatterns = ["Modules.*.dll", "Bootstrapper.dll", "BuildingBlocks.*.dll", "Api.dll"];

    // Las propiedades de ValkeyAreaOptions (src/Bootstrapper/Valkey/ValkeyOptions.cs).
    private static readonly string[] ValkeyAreas =
        ["Presence", "RateLimiting", "Sessions", "Permissions", "SendBudget", "Doorbells", "ApiKeyTouch"];

    /// <summary>
    /// Los <c>builder.Services.X</c> de <c>Program.cs</c> que <see cref="ReplayProgramRegistrations"/> repite. Un registro
    /// nuevo ahí que no entre en esa réplica dejaría su singleton fuera del inventario sin que nada se ponga rojo.
    /// </summary>
    private static readonly string[] ReplayedProgramRegistrations =
    [
        "AddProblemDetails", "AddExceptionHandler", "AddOpenApi", "AddQepPlatform", "AddScoped", "AddWebTelemetry",
        "AddQepHealthChecks", "AddQepForwardedHeaders", "AddQepCors", "AddMessagingRealtime", "AddRateLimiter",
    ];

    [Fact]
    public void EveryProgramRegistrationIsReplayed()
    {
        var program = File.ReadAllText(RepositoryFile("src/Api/Program.cs"));
        var missing = Regex.Matches(program, @"builder\.Services\.(\w+)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !ReplayedProgramRegistrations.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "Program.cs registra servicios que MultiReplicaInventoryTests no repite, así que sus singletons quedan fuera del "
            + "inventario D-6. Agrégalos a ReplayProgramRegistrations y a ReplayedProgramRegistrations: " + string.Join(", ", missing));
    }

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

    /// <summary>
    /// Un singleton registrado con una fábrica tipada a una interfaz (<c>AddSingleton&lt;IFoo&gt;(sp =&gt; new Foo(…))</c>) no
    /// deja ver su tipo concreto, así que el escaneo de estado no lo puede mirar. Cada uno nombra su concreto en
    /// <see cref="MultiReplicaInventory.InterfaceFactories"/>, y ese concreto pasa por el mismo escaneo.
    /// </summary>
    [Fact]
    public void EveryInterfaceTypedSingletonFactoryNamesItsConcreteType()
    {
        var listed = MultiReplicaInventory.InterfaceFactories.Select(entry => entry.ServiceTypeFullName).ToHashSet(StringComparer.Ordinal);
        var detected = InterfaceTypedSingletonFactories();
        var missing = detected.Where(name => !listed.Contains(name)).Order(StringComparer.Ordinal).ToList();
        var stale = listed.Where(name => !detected.Contains(name)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "Estos singletons se registran con una fábrica tipada a una interfaz y el inventario D-6 no ve su tipo concreto. "
            + "Agrégalos a MultiReplicaInventory.InterfaceFactories con el concreto que devuelve la fábrica, o tipa la fábrica "
            + "con el concreto: " + string.Join(", ", missing));
        Assert.True(stale.Count == 0,
            "Estas entradas de MultiReplicaInventory.InterfaceFactories ya no corresponden a una fábrica registrada; quítalas: "
            + string.Join(", ", stale));
        Assert.All(MultiReplicaInventory.InterfaceFactories, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Evidence), entry.ServiceTypeFullName);
            Assert.True(FindOurType(entry.ImplementationTypeFullName) is { IsInterface: false, IsAbstract: false },
                $"{entry.ImplementationTypeFullName} no es un tipo concreto de nuestros ensamblados.");
        });
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
        var violations = ReplicaViolations(
            File.ReadAllText(RepositoryFile("k8s/prod-deployment.yaml")),
            File.ReadAllText(RepositoryFile("k8s/prod-configMap.yaml")));

        Assert.True(violations.Count == 0, "Con más de una réplica: " + string.Join(" ", violations));
    }

    /// <summary>Las reglas de arriba, con manifiestos de juguete: con una réplica nada se exige.</summary>
    [Fact]
    public void OneReplicaRequiresNothingFromTheConfigMap() =>
        Assert.Empty(ReplicaViolations(Deployment(replicas: 1), "data:\n"));

    /// <summary>
    /// Un estado <c>SharedWithLocalFallback</c> sin su área de Valkey prendida en producción se comporta por pod: con dos
    /// réplicas es igual de inseguro que uno <c>Unsafe</c>.
    /// </summary>
    [Fact]
    public void MoreThanOneReplicaRequiresTheValkeySwitchOfEverySharedState()
    {
        var violations = ReplicaViolations(Deployment(replicas: 2), "data:\n  Seed__ExportLoad__Quotations: \"0\"\n");

        foreach (var key in MultiReplicaInventory.SharedStateSwitches.Values.Append("Valkey__Enabled"))
        {
            Assert.Contains(violations, violation => violation.Contains(key, StringComparison.Ordinal));
        }
    }

    /// <summary>Un interruptor presente pero en <c>"false"</c> vale lo mismo que ausente: una violación por clave.</summary>
    [Fact]
    public void MoreThanOneReplicaRejectsEveryValkeySwitchSetToFalse()
    {
        var keys = MultiReplicaInventory.SharedStateSwitches.Values.Append("Valkey__Enabled").ToList();
        var configMap = "data:\n  Seed__ExportLoad__Quotations: \"0\"\n" + string.Concat(keys.Select(key => $"  {key}: \"false\"\n"));

        var violations = ReplicaViolations(Deployment(replicas: 2), configMap);

        Assert.Equal(3, keys.Count);
        foreach (var key in keys)
        {
            Assert.Single(violations, violation => violation.StartsWith(key + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MoreThanOneReplicaPassesWithEverySwitchOn()
    {
        var switches = MultiReplicaInventory.SharedStateSwitches.Values.Append("Valkey__Enabled")
            .Select(key => $"  {key}: \"true\"\n");
        var configMap = "data:\n  Seed__ExportLoad__Quotations: \"0\"\n" + string.Concat(switches);

        Assert.DoesNotContain(ReplicaViolations(Deployment(replicas: 2), configMap),
            violation => !violation.Contains("Unsafe", StringComparison.Ordinal));
    }

    /// <summary>Todo estado compartido dice qué interruptor de Valkey lo comparte, y sólo esos tienen interruptor.</summary>
    [Fact]
    public void EverySharedStateNamesItsValkeySwitch()
    {
        var shared = MultiReplicaInventory.State
            .Where(entry => entry.Classification == StateClassification.SharedWithLocalFallback)
            .Select(entry => entry.TypeFullName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(shared, MultiReplicaInventory.SharedStateSwitches.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Lo que bloquea <c>replicas &gt; 1</c>: un <c>Unsafe</c>, <c>ExportLoadSeedWorker</c> prendido (<c>OffInProduction</c>)
    /// o un estado <c>SharedWithLocalFallback</c> sin su área de Valkey en <c>"true"</c> en el ConfigMap.
    /// </summary>
    private static List<string> ReplicaViolations(string deployment, string configMap)
    {
        var match = Regex.Match(deployment, @"(?m)^\s{2}replicas:\s*(\d+)\s*$");
        Assert.True(match.Success, "No se encontró `replicas:` en el Deployment.");
        var replicas = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var violations = new List<string>();
        if (replicas <= 1)
        {
            return violations;
        }

        violations.AddRange(MultiReplicaInventory.Workers
            .Where(entry => entry.Classification == WorkerClassification.Unsafe)
            .Select(entry => $"worker Unsafe: {entry.TypeFullName}."));
        violations.AddRange(MultiReplicaInventory.State
            .Where(entry => entry.Classification == StateClassification.Unsafe)
            .Select(entry => $"estado Unsafe: {entry.TypeFullName}."));

        // OffInProduction: con dos pods, ExportLoadSeedWorker sembraría dos veces; sólo vale apagado.
        if (!IsSet(configMap, "Seed__ExportLoad__Quotations", "0"))
        {
            violations.Add("Seed__ExportLoad__Quotations tiene que ser \"0\" en el ConfigMap.");
        }

        if (MultiReplicaInventory.SharedStateSwitches.Count > 0 && !IsSet(configMap, "Valkey__Enabled", "true"))
        {
            violations.Add("Valkey__Enabled tiene que ser \"true\" en el ConfigMap: sin eso, todo estado compartido es por pod.");
        }

        foreach (var (type, key) in MultiReplicaInventory.SharedStateSwitches)
        {
            if (!IsSet(configMap, key, "true"))
            {
                violations.Add($"{key} tiene que ser \"true\" en el ConfigMap: sin eso, {type} es por pod.");
            }
        }

        return violations;
    }

    private static bool IsSet(string configMap, string key, string value) =>
        Regex.IsMatch(configMap, $@"(?m)^\s*{Regex.Escape(key)}:\s*""{Regex.Escape(value)}""\s*$");

    private static string Deployment(int replicas) => $"spec:\n  replicas: {replicas}\n";

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

        // Lo que una fábrica tipada a una interfaz esconde: el concreto que declara InterfaceFactories.
        foreach (var entry in MultiReplicaInventory.InterfaceFactories)
        {
            if (FindOurType(entry.ImplementationTypeFullName) is { } concrete && HasState(concrete))
            {
                names.Add(concrete.FullName!);
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

    /// <summary>Singletons nuestros, fuera de los hosted services, cuya fábrica devuelve una interfaz o un tipo abstracto.</summary>
    private static HashSet<string> InterfaceTypedSingletonFactories() =>
        AllRegistrations()
            .Where(descriptor => descriptor.Lifetime == ServiceLifetime.Singleton
                && descriptor.ServiceType != typeof(IHostedService)
                && (descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory is not null : descriptor.ImplementationFactory is not null))
            .Select(ImplementationTypeOf)
            .OfType<Type>()
            .Where(type => (type.IsInterface || type.IsAbstract) && IsOurs(type))
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

    private static Type? FindOurType(string fullName) =>
        OurAssemblies().Select(assembly => assembly.GetType(fullName, throwOnError: false)).FirstOrDefault(type => type is not null);

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
            || assembly.StartsWith("BuildingBlocks", StringComparison.Ordinal)
            || assembly == "Api";
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
    /// Los registros de <c>Program.cs</c> con la configuración mínima de <c>CompositionRootTests</c>, más los mismos con
    /// todas las áreas de Valkey prendidas: lo que un interruptor agrega también tiene que estar clasificado.
    /// </summary>
    private static List<ServiceDescriptor> AllRegistrations()
    {
        var minimal = new ServiceCollection();
        ReplayProgramRegistrations(minimal, Configuration(allAreasOn: false));
        var allOn = new ServiceCollection();
        ReplayProgramRegistrations(allOn, Configuration(allAreasOn: true));
        return [.. minimal, .. allOn];
    }

    /// <summary>
    /// Lo mismo que hace <c>Program.cs</c> (<c>src/Api/Program.cs:70-88</c>) sobre <c>builder.Services</c>, en el mismo
    /// orden. <c>ApiExceptionHandler</c> y <c>SessionEstablisher</c> son <c>internal</c> de Api: se registran por
    /// reflexión, con el mismo método genérico. <see cref="EveryProgramRegistrationIsReplayed"/> amarra la lista.
    /// </summary>
    private static void ReplayProgramRegistrations(IServiceCollection services, IConfiguration configuration)
    {
        var api = typeof(global::Api.WebTelemetryMetrics).Assembly;
        services.AddProblemDetails();
        typeof(ExceptionHandlerServiceCollectionExtensions).GetMethods()
            .Single(method => method is { Name: "AddExceptionHandler", IsGenericMethodDefinition: true } && method.GetParameters().Length == 1)
            .MakeGenericMethod(api.GetType("Api.ApiExceptionHandler", throwOnError: true)!)
            .Invoke(null, [services]);
        // Por reflexión: una llamada directa activa el generador de comentarios XML de OpenAPI en este proyecto, que no es web.
        typeof(Microsoft.Extensions.DependencyInjection.OpenApiServiceCollectionExtensions)
            .GetMethod("AddOpenApi", [typeof(IServiceCollection)])!
            .Invoke(null, [services]);
        services.AddQepPlatform(configuration, new TestHostEnvironment());
        services.AddScoped(api.GetType("Api.SessionEstablisher", throwOnError: true)!);
        global::Api.WebTelemetryServiceCollectionExtensions.AddWebTelemetry(services);
        services.AddQepHealthChecks(configuration);
        services.AddQepForwardedHeaders(configuration);
        services.AddQepCors(configuration);
        services.AddMessagingRealtime();
        services.AddRateLimiter(_ => { });
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
            // El canal de correo de producción (k8s/prod-configMap.yaml) se registra con una fábrica propia.
            values["Notifications:EmailProvider"] = "infobip";
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
