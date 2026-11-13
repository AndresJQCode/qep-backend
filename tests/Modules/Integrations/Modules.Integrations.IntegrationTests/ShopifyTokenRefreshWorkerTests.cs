using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Shopify;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Spec 2026-11-12 §6.2 y §6.3 contra Postgres de verdad y <see cref="FakeShopifyHandler"/>: el worker refresca dentro del
/// margen y no antes, un solo refresco por tienda aunque corran dos vueltas, el reenlace no se pisa con el refresco, un 4xx
/// deja la conexión en <c>token_expired</c> desde cualquier estado recuperable y un guardado fallido se recupera solo.
/// </summary>
public sealed class ShopifyTokenRefreshWorkerTests
{
    private const string RefreshedAccess = "shpat_worker-refreshed-SENTINEL";
    private const string RefreshedRefresh = "shprt_worker-refreshed-SENTINEL";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Linked(QepApiFactory Factory, string ConnectionString, Guid TenantId, Guid ConnectionId, HttpClient Owner)
    {
        public ShopifyTokenRefreshWorker Worker =>
            Factory.Services.GetServices<IHostedService>().OfType<ShopifyTokenRefreshWorker>().Single();
    }

    /// <summary>Una tienda enlazada por HTTP, con el worker del host ya pasado por su primera vuelta.</summary>
    private static async Task<Linked> LinkAsync(QepApiFactory factory, string connectionString)
    {
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);
        var tenant = await RegisterTenantAsync(factory);
        var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var response = await SendAsync(
            owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId),
            new { installId = await ShopifyFlow.InstallAsync(factory, owner, tenant.TenantId) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        var linked = new Linked(factory, connectionString, tenant.TenantId, connection!.Id, owner);
        await linked.Worker.FirstRunCompletion;
        return linked;
    }

    private static Task<int> ExpireInAsync(Linked linked, TimeSpan left) =>
        ExecuteAsync(
            linked.ConnectionString,
            "UPDATE integrations.connections SET fields = jsonb_set(fields, '{tokenExpiresAt}', to_jsonb(@at::text)) WHERE id = @id",
            ("at", ShopifyTokenRefresh.Format(DateTimeOffset.UtcNow + left)),
            ("id", linked.ConnectionId));

    private static Task<int> SetStateAsync(Linked linked, string status, string? failureCode) =>
        ExecuteAsync(
            linked.ConnectionString,
            "UPDATE integrations.connections SET status = @status, last_failure_code = @code WHERE id = @id",
            ("status", status),
            ("code", (object?)failureCode ?? DBNull.Value),
            ("id", linked.ConnectionId));

    private static Task<string> StateAsync(Linked linked) =>
        ScalarAsync<string>(
            linked.ConnectionString,
            "SELECT status || ':' || coalesce(last_failure_code, '') FROM integrations.connections WHERE id = @id",
            ("id", linked.ConnectionId));

    private static async Task<string> SecretAsync(Linked linked, string fieldKey)
    {
        using var scope = linked.Factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>()
            .FindAsync(linked.TenantId, linked.ConnectionId, Ct);
        var secret = Assert.Single(connection!.Secrets, stored => stored.FieldKey == fieldKey);
        Assert.True(scope.ServiceProvider.GetRequiredService<ISecretProtector>()
            .TryUnprotect(linked.ConnectionId, fieldKey, secret.Protected, out var plaintext));
        return plaintext;
    }

    private static async Task WaitForRefreshRequestsAsync(FakeShopifyHandler shopify, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (shopify.RefreshRequests.Count < count)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The worker never called Shopify.");
            await Task.Delay(25, Ct);
        }
    }

    private static (int, int, int, int) Counts(ShopifyTokenRefreshRunResult result) =>
        (result.Refreshed, result.Rejected, result.Deferred, result.Skipped);

    [Fact]
    public async Task ATokenIsRefreshedWithin20MinutesAndNotBefore()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh);
        var verifiedBefore = await ScalarAsync<string>(
            connectionString, "SELECT last_verified_at::text FROM integrations.connections WHERE id = @id", ("id", linked.ConnectionId));

        await ExpireInAsync(linked, TimeSpan.FromMinutes(21));
        Assert.Equal((0, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Empty(factory.ShopifyHandler.RefreshRequests);

        await ExpireInAsync(linked, TimeSpan.FromMinutes(19));
        Assert.Equal((1, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));

        var refresh = Assert.Single(factory.ShopifyHandler.RefreshRequests);
        Assert.Contains($"refresh_token={ShopifyFlow.SentinelRefreshToken}", refresh.Body, StringComparison.Ordinal);
        Assert.Equal(RefreshedAccess, await SecretAsync(linked, ShopifyFieldKeys.AccessToken));
        Assert.Equal(RefreshedRefresh, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));
        var expiresAt = DateTimeOffset.Parse(
            await ScalarAsync<string>(
                connectionString, "SELECT fields->>'tokenExpiresAt' FROM integrations.connections WHERE id = @id", ("id", linked.ConnectionId)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.InRange(expiresAt, DateTimeOffset.UtcNow.AddMinutes(50), DateTimeOffset.UtcNow.AddMinutes(70));
        Assert.Equal("Active:", await StateAsync(linked));
        Assert.Equal(verifiedBefore, await ScalarAsync<string>(
            connectionString, "SELECT last_verified_at::text FROM integrations.connections WHERE id = @id", ("id", linked.ConnectionId)));
    }

    // Sin la app, el refresco mandaría client_id vacío, Shopify respondería un 4xx y todas las tiendas quedarían en
    // token_expired, que sólo se recupera reautorizando. La vuelta se salta entera, como ConnectInstagram y el signup.
    [Fact]
    public async Task WithoutTheShopifyAppTheRunTouchesNoConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, HttpStatusCode.BadRequest);
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));

        using var withoutApp = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Shopify:App:ClientId", string.Empty);
            builder.UseSetting("Shopify:App:ClientSecret", string.Empty);
        });
        var worker = withoutApp.Services.GetServices<IHostedService>().OfType<ShopifyTokenRefreshWorker>().Single();
        await worker.FirstRunCompletion;

        Assert.Equal((0, 0, 0, 0), Counts(await worker.RunOnceAsync(Ct)));
        Assert.Empty(factory.ShopifyHandler.RefreshRequests);
        Assert.Equal("Active:", await StateAsync(linked));
        Assert.Equal(ShopifyFlow.SentinelRefreshToken, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));
    }

    // Anti-objetivo 1: el refresh token rota, así que dos refrescos de la misma tienda dejarían guardado un par retirado.
    [Fact]
    public async Task TwoConcurrentRunsCallShopifyOnce()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, delay: TimeSpan.FromSeconds(2));
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));

        var results = await Task.WhenAll(linked.Worker.RunOnceAsync(Ct), linked.Worker.RunOnceAsync(Ct));

        Assert.Single(factory.ShopifyHandler.RefreshRequests);
        Assert.Equal((1, 0, 0, 1), (results.Sum(r => r.Refreshed), results.Sum(r => r.Rejected), results.Sum(r => r.Deferred), results.Sum(r => r.Skipped)));
        Assert.Equal(RefreshedRefresh, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));
    }

    // §6.2: el reenlace toma el mismo candado; el par que queda es el del reenlace y la vuelta siguiente no refresca.
    [Fact]
    public async Task RelinkingWhileTheWorkerRefreshesKeepsTheLinkTokens()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        factory.ShopifyHandler.Reset();
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, delay: TimeSpan.FromSeconds(2));
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler, token: "shpat_relink-SENTINEL", refreshToken: "shprt_relink-SENTINEL");
        var secondInstall = await ShopifyFlow.InstallAsync(factory, linked.Owner, linked.TenantId);
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));

        var run = linked.Worker.RunOnceAsync(Ct);
        await WaitForRefreshRequestsAsync(factory.ShopifyHandler, 1);
        var relinked = await SendAsync(linked.Owner, HttpMethod.Post, ShopifyFlow.LinkUrl(linked.TenantId), new { installId = secondInstall });
        await run;

        Assert.Equal(HttpStatusCode.Created, relinked.StatusCode);
        Assert.Equal("shpat_relink-SENTINEL", await SecretAsync(linked, ShopifyFieldKeys.AccessToken));
        Assert.Equal("shprt_relink-SENTINEL", await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));
        Assert.Equal((0, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Single(factory.ShopifyHandler.RefreshRequests);
    }

    [Fact]
    public async Task A401MarksTokenExpiredAndA503ChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));

        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, HttpStatusCode.ServiceUnavailable);
        Assert.Equal((0, 0, 1, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Equal("Active:", await StateAsync(linked));
        Assert.Equal(ShopifyFlow.SentinelRefreshToken, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));

        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, HttpStatusCode.Unauthorized);
        Assert.Equal((0, 1, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Equal("NeedsAttention:token_expired", await StateAsync(linked));

        Assert.Equal((0, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Equal(2, factory.ShopifyHandler.RefreshRequests.Count);
    }

    // La decisión del owner cubre sólo 4xx salvo 429: un 3xx (una tienda renombrada, con AllowAutoRedirect apagado) o un 429
    // esperan a la próxima vuelta. token_expired no se recupera sin reautorizar, así que no se gasta en algo que no lo prueba.
    [Theory]
    [InlineData(HttpStatusCode.Found, "Active:", 0, 1)]
    [InlineData(HttpStatusCode.MovedPermanently, "Active:", 0, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, "Active:", 0, 1)]
    [InlineData(HttpStatusCode.BadRequest, "NeedsAttention:token_expired", 1, 0)]
    public async Task OnlyA4xxOtherThan429MarksTokenExpired(HttpStatusCode status, string state, int rejected, int deferred)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, status);

        Assert.Equal((0, rejected, deferred, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Equal(state, await StateAsync(linked));
        Assert.Equal(ShopifyFlow.SentinelRefreshToken, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));
    }

    // Una conexión que lanza algo inesperado (acá, un dominio que el gateway no acepta) no tumba la vuelta: la lista no tiene
    // orden, así que una que falla siempre dejaría sin refrescar a las que vienen detrás.
    [Fact]
    public async Task AConnectionThatThrowsDoesNotStopTheOthers()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var broken = await LinkAsync(factory, connectionString);
        using var brokenOwner = broken.Owner;
        // La ruta se libera para enlazar la misma tienda a otro tenant; la conexión vieja queda con un dominio ilegible.
        await ExecuteAsync(connectionString, "DELETE FROM integrations.connection_routes WHERE connection_id = @id", ("id", broken.ConnectionId));
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET fields = jsonb_set(fields, '{shopDomain}', to_jsonb('no es un dominio'::text)) WHERE id = @id",
            ("id", broken.ConnectionId));
        var healthy = await LinkAsync(factory, connectionString);
        using var healthyOwner = healthy.Owner;
        await ExpireInAsync(broken, TimeSpan.FromMinutes(5));
        await ExpireInAsync(healthy, TimeSpan.FromMinutes(5));
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh);

        Assert.Equal((1, 0, 1, 0), Counts(await healthy.Worker.RunOnceAsync(Ct)));
        Assert.Single(factory.ShopifyHandler.RefreshRequests);
        Assert.Equal(RefreshedAccess, await SecretAsync(healthy, ShopifyFieldKeys.AccessToken));
        Assert.Equal("Active:", await StateAsync(broken));
    }

    // Review Focus 1 (D9): ReportCredentialsRejectedAsync ignoraría la pausada y el worker la reintentaría para siempre.
    [Fact]
    public async Task APausedConnectionRejectedIsMarkedAndNotRetried()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await SetStateAsync(linked, "Paused", null);
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh, HttpStatusCode.Unauthorized);

        Assert.Equal((0, 1, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Equal("NeedsAttention:token_expired", await StateAsync(linked));
        Assert.Equal((0, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Single(factory.ShopifyHandler.RefreshRequests);
    }

    // Review Focus 2: refrescar no saca de NeedsAttention; eso lo hace una verificación.
    [Fact]
    public async Task AWebhooksNotRegisteredConnectionIsRefreshedAndKeepsItsState()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await SetStateAsync(linked, "NeedsAttention", ConnectionFailureCodes.WebhooksNotRegistered);
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh);

        Assert.Equal((1, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Equal("NeedsAttention:webhooks_not_registered", await StateAsync(linked));
        Assert.Equal(RefreshedAccess, await SecretAsync(linked, ShopifyFieldKeys.AccessToken));
    }

    [Theory]
    [InlineData("token_expired")]
    [InlineData("app_uninstalled")]
    public async Task TokenExpiredAndAppUninstalledAreNotRefreshed(string failureCode)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await SetStateAsync(linked, "NeedsAttention", failureCode);
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh);

        Assert.Equal((0, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Empty(factory.ShopifyHandler.RefreshRequests);
        Assert.Equal($"NeedsAttention:{failureCode}", await StateAsync(linked));
    }

    // §6.3: una conexión de prueba anterior a este cambio no tiene con qué refrescar; la persona reconecta.
    [Fact]
    public async Task AConnectionWithoutARefreshTokenIsMarkedTokenExpired()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await ExecuteAsync(
            connectionString,
            "DELETE FROM integrations.connection_secrets WHERE connection_id = @id AND field_key = 'refreshToken'",
            ("id", linked.ConnectionId));

        Assert.Equal((0, 1, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Empty(factory.ShopifyHandler.RefreshRequests);
        Assert.Equal("NeedsAttention:token_expired", await StateAsync(linked));
    }

    // §6.2: si el guardado falla después de que Shopify rotó, el par viejo sigue sirviendo (nadie usó el nuevo) y la vuelta
    // siguiente refresca con él.
    [Fact]
    public async Task AFailedSaveAfterTheRefreshRecoversOnTheNextRun()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var linked = await LinkAsync(factory, connectionString);
        using var _ = linked.Owner;
        await ExpireInAsync(linked, TimeSpan.FromMinutes(5));
        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, "shpat_lost-SENTINEL", "shprt_lost-SENTINEL", delay: TimeSpan.FromSeconds(2));

        var run = linked.Worker.RunOnceAsync(Ct);
        await WaitForRefreshRequestsAsync(factory.ShopifyHandler, 1);
        await ExecuteAsync(connectionString, "UPDATE integrations.connections SET version = version + 1 WHERE id = @id", ("id", linked.ConnectionId));
        Assert.Equal((0, 0, 1, 0), Counts(await run));
        Assert.Equal(ShopifyFlow.SentinelRefreshToken, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));

        factory.ShopifyHandler.ScriptRefresh(ShopifyFlow.Shop, RefreshedAccess, RefreshedRefresh);
        Assert.Equal((1, 0, 0, 0), Counts(await linked.Worker.RunOnceAsync(Ct)));
        Assert.Contains(
            $"refresh_token={ShopifyFlow.SentinelRefreshToken}", factory.ShopifyHandler.RefreshRequests[^1].Body, StringComparison.Ordinal);
        Assert.Equal(RefreshedRefresh, await SecretAsync(linked, ShopifyFieldKeys.RefreshToken));
    }
}
