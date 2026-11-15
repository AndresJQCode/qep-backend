using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Un Valkey de prueba sin ACL en puerto aleatorio: copia mínima de la de <c>Bootstrapper.UnitTests</c>. Usa el
/// <c>ContainerBuilder</c> genérico y no <c>Testcontainers.Redis</c> (V2 del plan de Valkey). El Valkey local del developer
/// (<c>localhost:6379</c>) no se toca.
/// </summary>
internal static class ValkeyTestServer
{
    private const string Image = "valkey/valkey:9.1.3-alpine";
    private const int Port = 6379;

    internal static async Task<IContainer> StartAsync(CancellationToken ct)
    {
        var container = new ContainerBuilder(Image)
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await container.StartAsync(ct);
        return container;
    }

    internal static string ConnectionString(IContainer container) =>
        $"{container.Hostname}:{container.GetMappedPublicPort(Port)},abortConnect=false";
}
