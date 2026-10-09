using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-09 §6.1, «Migraciones de Integrations»: el único (provider_key, external_id) impide que
/// un número quede conectado en dos tenants; borrar la conexión libera la ruta (cascada); el puerto
/// cross-tenant devuelve tenant, conexión y estado.
/// </summary>
public sealed class ConnectionRoutesApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameNumberCannotBeRoutedToTwoConnections()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var first = await SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Ventas", phoneNumberId: "111", wabaId: "222");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Otro", phoneNumberId: "111", wabaId: "999"));

        Assert.Equal(IntegrationsErrorCodes.WhatsAppNumberAlreadyConnected, error.Code);
        using var scope = factory.Services.CreateScope();
        var routes = scope.ServiceProvider.GetRequiredService<IConnectionRoutes>();
        var route = await routes.FindAsync("whatsapp-cloud", "111", Ct);
        Assert.NotNull(route);
        Assert.Equal(first, route.ConnectionId);
        Assert.Equal(ConnectionStatus.Active, route.Status);
        Assert.Null(await routes.FindAsync("whatsapp-cloud", "000", Ct));
        var byAccount = await routes.FindByAccountAsync("whatsapp-cloud", "222", Ct);
        Assert.Equal([first], byAccount.Select(entry => entry.ConnectionId));
    }

    [Fact]
    public async Task DeletingTheConnectionFreesTheRoute()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var id = await SeedWhatsAppConnectionAsync(factory, tenantId, "Ventas", phoneNumberId: "111", wabaId: "222");

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
            repository.Remove((await repository.FindAsync(tenantId, id, Ct))!);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.connection_routes"));
        var again = await SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Ventas", phoneNumberId: "111", wabaId: "222");
        Assert.NotEqual(id, again);
    }

    // ListByProviderAsync trae todas (no sólo Active): la bandeja muestra el nombre también en una pausada.
    [Fact]
    public async Task ListByProviderReturnsEveryStatusOfTheTenantOnly()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var active = await SeedWhatsAppConnectionAsync(factory, tenantId, "Ventas", "111", "222");
        var paused = await SeedWhatsAppConnectionAsync(factory, tenantId, "Soporte", "333", "222");
        await SeedWhatsAppConnectionAsync(factory, Guid.CreateVersion7(), "Ajeno", "444", "555");
        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>();
            (await repository.FindAsync(tenantId, paused, Ct))!.Pause(DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
        }

        using var scope2 = factory.Services.CreateScope();
        var listing = await scope2.ServiceProvider.GetRequiredService<IIntegrationConnections>()
            .ListByProviderAsync(tenantId, "whatsapp-cloud", Ct);

        Assert.Equal(
            [(paused, "Soporte", ConnectionStatus.Paused), (active, "Ventas", ConnectionStatus.Active)],
            listing.Select(entry => (entry.Id, entry.Name, entry.Status)));
    }
}
