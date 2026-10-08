using System.Reflection;
using Bootstrapper;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Authorization.Application;

namespace ArchitectureTests;

/// <summary>
/// El registro de handlers en el composition root es **a mano, uno por uno**. No hay escaneo de
/// ensamblados, así que un caso de uso nuevo cuyo endpoint se mapea pero cuyo handler nadie
/// registra compila, arranca y responde **500** — no 404, no un error de arranque: 500 en
/// producción, con el mensaje `No service for type ICommandHandler&lt;...&gt;`.
///
/// El ledger declaró ese riesgo textualmente al cerrar `CAT-06`, y **volvió a pasar igual**:
/// `PublishFileHandler` y `UnpublishFileHandler` nunca se registraron, así que el `PUT` y el
/// `DELETE` de `/files/{id}/publication` respondieron 500 desde el día que se escribieron. Y como
/// `imageUrl` de producto sólo tiene valor si el archivo fue publicado, la mitad de lectura de
/// `CAT-05b` estuvo muerta sin que ninguna prueba lo notara.
///
/// Advertirlo en un documento ya se demostró insuficiente. Esta prueba lo convierte en regla.
/// </summary>
public sealed class CompositionRootTests
{
    [Fact]
    public void EveryCommandAndQueryHasItsHandlerRegistered()
    {
        var registeredServices = BuildPlatformServices()
            .Select(descriptor => descriptor.ServiceType)
            .ToHashSet();

        var unregistered = MessageTypes()
            .Where(message => !registeredServices.Contains(HandlerTypeFor(message)))
            .Select(message => $"{message.Message.Name} -> {HandlerTypeFor(message).Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            "Estos casos de uso no tienen su handler registrado en QepServiceCollectionExtensions, "
                + "así que su endpoint responde 500: "
                + string.Join(", ", unregistered));
    }

    /// <summary>
    /// Sin al menos un mensaje encontrado, la prueba de arriba pasaría por vacía y no verificaría
    /// nada. Esto ancla que el descubrimiento por reflexión sigue viendo los ensamblados.
    /// </summary>
    [Fact]
    public void MessageDiscoveryFindsTheApplicationAssemblies()
    {
        Assert.NotEmpty(ApplicationAssemblies());
        Assert.NotEmpty(MessageTypes());
    }

    private static Type HandlerTypeFor(MessageType message) =>
        (message.IsCommand ? typeof(ICommandHandler<,>) : typeof(IQueryHandler<,>))
            .MakeGenericType(message.Message, message.Response);

    /// <summary>
    /// El contenedor puede construir de verdad lo que decide permisos.
    /// </summary>
    /// <remarks>
    /// Los demas casos de este archivo miran **que quedo registrado**; este **construye**. Hace
    /// falta porque <c>ITenantRoleCatalog</c> es la unica pieza scoped que depende de un
    /// DbContext propio y de un singleton a la vez: un registro mal armado ahi no rompe el
    /// build ni aparece en la lista de descriptores, aparece como 500 en el primer request
    /// autenticado — que es cada request.
    ///
    /// Y las pruebas de integracion, que lo cubririan levantando la app, necesitan Docker.
    /// </remarks>
    [Fact]
    public void TheContainerCanBuildTheTenantRoleCatalog()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var catalog = scope.ServiceProvider.GetRequiredService<ITenantRoleCatalog>();

        Assert.IsType<TenantRoleCatalog>(catalog);
    }

    /// <summary>
    /// La vista por tenant NO puede ser singleton: fusiona los roles que el tenant definio, y
    /// esos cambian con un PUT, no con un deploy. Registrada como singleton, el primer
    /// tenant en preguntar dejaria su catalogo cacheado para todo el proceso — y para todos
    /// los demas tenants.
    /// </summary>
    [Fact]
    public void TheTenantRoleCatalogIsScopedAndTheSystemOneIsNot()
    {
        var services = BuildPlatformServices();

        Assert.Equal(
            ServiceLifetime.Scoped,
            services.Single(d => d.ServiceType == typeof(ITenantRoleCatalog)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Singleton,
            services.Single(d => d.ServiceType == typeof(IRoleCatalog)).Lifetime);
    }

    /// <summary>
    /// Spec 2026-10-07, criterio 4: un permiso sin módulo declarado se enmascara siempre, así que
    /// olvidarlo deja a todos sin ese permiso en silencio. Como el registro es a mano, esta prueba
    /// lo convierte en regla: toda constante de toda clase <c>*Permissions</c> de los ensamblados
    /// Application tiene su <see cref="PermissionDefinition"/> con <c>RequiredModules</c> no nulo.
    /// </summary>
    [Fact]
    public void EveryPermissionConstantDeclaresItsModules()
    {
        var definitions = RegisteredPermissionDefinitions()
            .GroupBy(definition => definition.Permission, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var unmapped = PermissionConstants()
            .Where(permission =>
                !definitions.TryGetValue(permission, out var definition) || definition.RequiredModules is null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unmapped.Length == 0,
            "Estos permisos no tienen PermissionDefinition con RequiredModules en "
                + "QepServiceCollectionExtensions, así que se enmascaran siempre: "
                + string.Join(", ", unmapped));
    }

    /// <summary>
    /// <see cref="ModuleEntitlementMask"/> indexa por nombre con <c>ToDictionary</c>: un permiso
    /// registrado dos veces tumbaría el singleton al construirse. Esta prueba lo nombra antes.
    /// </summary>
    [Fact]
    public void EveryRegisteredPermissionStringIsUnique()
    {
        var duplicated = RegisteredPermissionDefinitions()
            .GroupBy(definition => definition.Permission, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicated.Length == 0,
            "Estos permisos están registrados más de una vez como PermissionDefinition: "
                + string.Join(", ", duplicated));
    }

    /// <summary>Ningún permiso de un rol de sistema sale del fallback de metadata de RoleCatalog.</summary>
    [Fact]
    public void NoSystemRolePermissionComesFromTheMetadataFallback()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();
        var catalog = provider.GetRequiredService<IRoleCatalog>();

        Assert.Empty(catalog.ListPermissions()
            .Where(permission => permission.RequiredModules is null)
            .Select(permission => permission.Permission));
    }

    /// <summary>Ancla: sin esto, las dos de arriba pasarían por vacías. Son las 35 del spec; el spec
    /// de POS sube este número cuando declare las suyas.</summary>
    [Fact]
    public void PermissionDiscoveryFindsTheThirtyFiveConstants()
    {
        Assert.Equal(35, PermissionConstants().Length);
    }

    [Fact]
    public void TheContainerCanBuildTheEntitlementMask()
    {
        using var provider = BuildPlatformServices().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ModuleEntitlementMask>());
    }

    private static PermissionDefinition[] RegisteredPermissionDefinitions() =>
        BuildPlatformServices()
            .Where(descriptor => descriptor.ServiceType == typeof(PermissionDefinition))
            .Select(descriptor => (PermissionDefinition)descriptor.ImplementationInstance!)
            .ToArray();

    private static string[] PermissionConstants() =>
        ApplicationAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                type is { IsAbstract: true, IsSealed: true } &&
                type.Name.EndsWith("Permissions", StringComparison.Ordinal))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static ServiceCollection BuildPlatformServices()
    {
        var services = new ServiceCollection();
        services.AddQepPlatform(MinimalConfiguration(), new TestHostEnvironment());
        return services;
    }

    // Valores de juguete: esta prueba mira **qué quedó registrado**, no construye ningún
    // servicio, así que nada de esto se conecta a nada. Sólo tiene que alcanzar para que los
    // AddXInfrastructure no aborten mientras registran.
    private static IConfiguration MinimalConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] =
                    "Host=localhost;Port=5432;Database=architecture_tests;Username=x;Password=x",
                ["Storage:R2:AccountId"] = "account",
                ["Storage:R2:AccessKeyId"] = "key",
                ["Storage:R2:SecretAccessKey"] = "secret",
                ["Storage:R2:Bucket"] = "bucket",
                ["Notifications:EmailProvider"] = "log",
            })
            .Build();

    private static MessageType[] MessageTypes() =>
        ApplicationAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces().Select(contract => (type, contract)))
            .Where(pair => pair.contract.IsGenericType)
            .Select(pair => new
            {
                pair.type,
                definition = pair.contract.GetGenericTypeDefinition(),
                response = pair.contract.GetGenericArguments()[0],
            })
            .Where(pair =>
                pair.definition == typeof(ICommand<>) || pair.definition == typeof(IQuery<>))
            .Select(pair => new MessageType(
                pair.type, pair.response, pair.definition == typeof(ICommand<>)))
            .ToArray();

    // Se cargan por patrón de archivo y no por un tipo ancla de cada módulo: un ancla hay que
    // acordarse de agregarla al abrir un módulo nuevo, y justamente el olvido es lo que esta
    // prueba existe para atrapar.
    private static Assembly[] ApplicationAssemblies() =>
        Directory
            .GetFiles(AppContext.BaseDirectory, "Modules.*.Application.dll")
            .Select(Assembly.LoadFrom)
            .ToArray();

    private sealed record MessageType(Type Message, Type Response, bool IsCommand);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ArchitectureTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
