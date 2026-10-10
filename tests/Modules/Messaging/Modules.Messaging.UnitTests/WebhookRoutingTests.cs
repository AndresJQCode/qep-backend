using Microsoft.Extensions.Caching.Memory;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Webhook;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §8.2 (D-M18): la ruta encontrada se cachea 60 s; una ruta que no existe no se
/// cachea, para que un número recién conectado no pierda un minuto de mensajes.</summary>
public sealed class WebhookRoutingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMissIsNotCachedSoTheNextCallFindsTheNewRoute()
    {
        var directory = new FakeDirectory();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var routing = new WebhookRouting(directory, new NoTenantModules(), cache);

        Assert.Null(await routing.FindRouteAsync("111", Ct));
        var route = new MessagingRoute(Guid.CreateVersion7(), Guid.CreateVersion7(), "Active");
        directory.Route = route;

        Assert.Equal(route, await routing.FindRouteAsync("111", Ct));
        Assert.Equal(2, directory.Calls);
    }

    [Fact]
    public async Task AHitIsCached()
    {
        var directory = new FakeDirectory { Route = new MessagingRoute(Guid.CreateVersion7(), Guid.CreateVersion7(), "Active") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var routing = new WebhookRouting(directory, new NoTenantModules(), cache);

        await routing.FindRouteAsync("111", Ct);
        await routing.FindRouteAsync("111", Ct);

        Assert.Equal(1, directory.Calls);
    }

    private sealed class FakeDirectory : IMessagingConnectionDirectory
    {
        public MessagingRoute? Route { get; set; }

        public int Calls { get; private set; }

        public Task<MessagingRoute?> FindRouteAsync(string phoneNumberId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Route);
        }

        public Task<IReadOnlyList<MessagingRoute>> FindByAccountAsync(string wabaId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MessagingSender?> ResolveSenderAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, string>> ListNamesAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportRejectedAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoTenantModules : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }
}
