using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>Dueños de inventario (RF-005, §10.1) de punta a punta por HTTP.</summary>
public sealed class OwnerApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string OwnersUrl => $"{WmsUrl()}/owners";

    private static HttpRequestMessage Write(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Client", "web");
        return request;
    }

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(OwnersUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await BodyAsync(response)).RootElement.Clone();
    }

    private static async Task<JsonElement> CreateClientOwnerAsync(HttpClient client, string name = "Acme")
    {
        var response = await client.SendAsync(
            Write(HttpMethod.Post, OwnersUrl, new { name, identification = "900123", contactEmail = "acme@example.com" }), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await BodyAsync(response)).RootElement.Clone();
    }

    private static object UpdateBody(string name, bool isActive, long version) =>
        new { name, identification = "900123", contactEmail = "acme@example.com", isActive, version };

    private sealed class OwnerWithStockProbe : IOwnerStockProbe
    {
        public Task<bool> HasStockAsync(Guid tenantId, Guid ownerId, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task ListingANewTenantReturnsOnlyTheSelfOwner()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var items = (await ListAsync(client)).GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Self", items[0].GetProperty("kind").GetString());
        Assert.Equal("Propio", items[0].GetProperty("name").GetString());
        Assert.True(items[0].GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task CreatingAClientOwnerReturns201AndAudits()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(
            Write(HttpMethod.Post, OwnersUrl, new { name = "Acme", identification = "900123", contactEmail = "acme@example.com" }), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var root = document.RootElement;
        var id = root.GetProperty("id").GetGuid();
        Assert.Equal("Client", root.GetProperty("kind").GetString());
        Assert.Equal("Acme", root.GetProperty("name").GetString());
        Assert.Equal("acme@example.com", root.GetProperty("contactEmail").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.owner.created", id.ToString()));
        Assert.Equal(2, (await ListAsync(client)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task DeactivatingTheSelfOwnerIs422SelfImmutable()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var self = (await ListAsync(client)).GetProperty("items")[0];

        var response = await client.SendAsync(
            Write(
                HttpMethod.Put,
                $"{OwnersUrl}/{self.GetProperty("id").GetGuid()}",
                UpdateBody("Propio", false, self.GetProperty("version").GetInt64())),
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.owner.self_immutable", await CodeOfAsync(response));
    }

    [Fact]
    public async Task DeactivatingAClientWorksAndAStaleVersionIs412()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateClientOwnerAsync(client);
        var id = created.GetProperty("id").GetGuid();

        var response = await client.SendAsync(Write(HttpMethod.Put, $"{OwnersUrl}/{id}", UpdateBody("Acme SA", false, 1)), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.False(document.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal("Acme SA", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(factory, "wms.owner.updated", id.ToString()));

        var stale = await client.SendAsync(Write(HttpMethod.Put, $"{OwnersUrl}/{id}", UpdateBody("Acme SA", true, 1)), Ct);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task DeactivatingAClientWithStockIs422()
    {
        var (database, factory) = await OpenAsync(configureServices: services => services.AddScoped<IOwnerStockProbe, OwnerWithStockProbe>());
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateClientOwnerAsync(client);
        var id = created.GetProperty("id").GetGuid();

        var response = await client.SendAsync(Write(HttpMethod.Put, $"{OwnersUrl}/{id}", UpdateBody("Acme", false, 1)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("wms.owner.has_stock", await CodeOfAsync(response));
        var items = (await ListAsync(client)).GetProperty("items");
        var owner = items.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);
        Assert.True(owner.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task AnInvalidEmailIs422ValidationFailedWithTheField()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Write(HttpMethod.Post, OwnersUrl, new { name = "Acme", contactEmail = "no-es-un-correo" }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("ContactEmail", out var emailErrors));
    }

    [Fact]
    public async Task AnUnknownOwnerIs404()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Write(HttpMethod.Put, $"{OwnersUrl}/{Guid.NewGuid()}", UpdateBody("Acme", true, 1)), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("wms.owner.not_found", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AnOverlongNameIs422ValidationFailedInsteadOfAServerError()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);

        var response = await client.SendAsync(Write(HttpMethod.Post, OwnersUrl, new { name = new string('x', 121) }), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("Name", out var nameErrors));
    }
}
