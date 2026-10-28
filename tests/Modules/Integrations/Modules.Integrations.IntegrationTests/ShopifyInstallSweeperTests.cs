using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Infrastructure.Shopify;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §9: el barrido borra las instalaciones vencidas, sin canje o sin enlace. La fila reclamada y
/// sin token (<c>claimed.…</c>) es un canje en vuelo y no se toca hasta <see cref="ShopifyInstallSweeper.ClaimedGrace"/>
/// después de vencer.
/// </summary>
public sealed class ShopifyInstallSweeperTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ShopifyInstallSweeper SweeperOf(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<IHostedService>().OfType<ShopifyInstallSweeper>().Single();

    /// <param name="expiresIn">Relativo a <c>now()</c> de la base; negativo = ya venció.</param>
    private static async Task<Guid> SeedAsync(string connectionString, string state, bool exchanged, TimeSpan expiresIn)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO integrations.shopify_installs
                (id, shop_domain, state, redirect_uri, initiated_tenant_id, initiated_by, shop_id, shop_name, scopes,
                 access_token_protected, key_id, created_at, expires_at)
            VALUES (@id, 'mi-tienda.myshopify.com', @state, 'https://qep.qcode.co/shopify/callback', NULL, NULL,
                    CASE WHEN @exchanged THEN '12345678' END, CASE WHEN @exchanged THEN 'Mi Tienda' END,
                    CASE WHEN @exchanged THEN 'read_orders' END,
                    CASE WHEN @exchanged THEN '\x0102'::bytea END, CASE WHEN @exchanged THEN 'test' END,
                    now() - interval '1 day', now() + make_interval(secs => @seconds))
            """,
            ("id", id),
            ("state", state),
            ("exchanged", exchanged),
            ("seconds", expiresIn.TotalSeconds));
        return id;
    }

    private static Task<long> ExistsAsync(string connectionString, Guid id) =>
        ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.shopify_installs WHERE id = @id", ("id", id));

    [Fact]
    public async Task SweepDeletesExpiredInstallsButKeepsAClaimedExchangeInFlight()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var sweeper = SweeperOf(factory);
        await sweeper.FirstRunCompletion.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var startedExpired = await SeedAsync(connectionString, "c3RhcnRlZC1leHBpcmVk", exchanged: false, TimeSpan.FromMinutes(-1));
        var exchangedExpired = await SeedAsync(connectionString, "claimed.ZXhjaGFuZ2VkLWV4cGlyZWQ", exchanged: true, TimeSpan.FromMinutes(-1));
        var claimedInFlight = await SeedAsync(connectionString, "claimed.aW4tZmxpZ2h0", exchanged: false, TimeSpan.FromMinutes(-2));
        var claimedAbandoned = await SeedAsync(connectionString, "claimed.YWJhbmRvbmVk", exchanged: false, TimeSpan.FromMinutes(-11));
        var startedValid = await SeedAsync(connectionString, "c3RhcnRlZC12YWxpZA", exchanged: false, TimeSpan.FromMinutes(10));
        var exchangedValid = await SeedAsync(connectionString, "claimed.ZXhjaGFuZ2VkLXZhbGlk", exchanged: true, TimeSpan.FromDays(6));

        var removed = await sweeper.RunOnceAsync(Ct);

        Assert.Equal(3, removed);
        Assert.Equal(0, await ExistsAsync(connectionString, startedExpired));
        Assert.Equal(0, await ExistsAsync(connectionString, exchangedExpired));
        Assert.Equal(0, await ExistsAsync(connectionString, claimedAbandoned));
        Assert.Equal(1, await ExistsAsync(connectionString, claimedInFlight));
        Assert.Equal(1, await ExistsAsync(connectionString, startedValid));
        Assert.Equal(1, await ExistsAsync(connectionString, exchangedValid));
        Assert.Equal(0, await sweeper.RunOnceAsync(Ct));
    }

    // El margen tiene que cubrir las dos llamadas del canje (canje y tienda) con holgura, y el periodo es el de §9.
    [Fact]
    public void GraceCoversTheExchangeCallsAndTheSweepRunsHourly()
    {
        Assert.True(ShopifyInstallSweeper.ClaimedGrace >= 2 * ShopifyAdminGateway.Timeout * 10);
        Assert.Equal(TimeSpan.FromHours(1), ShopifyInstallSweeper.Interval);
    }
}
