using System.Reflection;
using Bootstrapper;
using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

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
