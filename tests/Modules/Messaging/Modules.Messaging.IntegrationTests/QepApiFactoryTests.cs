using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>El host de prueba no vigila sus archivos de configuración. Con <c>reloadOnChange</c> cada host abre un
/// <c>FileSystemWatcher</c> sobre el directorio de los user-secrets que nunca se libera (el <c>PhysicalFileProvider</c> que crea
/// <c>FileConfigurationSource.ResolveFileProvider</c> no lo dispone nadie), y en macOS un proceso admite 512 streams de FSEvents:
/// pasado ese número de hosts, el siguiente se queda en un bucle de recarga dentro de <c>WebApplication.CreateBuilder</c> y la
/// prueba muere con «Timed out waiting for the entry point to build the IHost».</summary>
public sealed class QepApiFactoryTests
{
    [Fact]
    public async Task TheHostDoesNotWatchItsConfigurationFiles()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());

        var configuration = (IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>();
        var files = configuration.Providers.OfType<FileConfigurationProvider>().ToList();

        Assert.NotEmpty(files);
        Assert.Empty(files.Where(provider => provider.Source.ReloadOnChange).Select(provider => provider.Source.Path));
    }
}
