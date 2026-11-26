using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Buyers.Application;
using Npgsql;
using static Modules.Store.IntegrationTests.PublicStoreFixture;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Los adaptadores de Bootstrapper que le dan a Buyers el host y el nombre de la tienda: con la tienda real y Postgres, sólo
/// Turnstile es falso. Necesitan Docker.
/// </summary>
public sealed class BuyerStorefrontAdapterApiTests
{
    private const string StoreName = "Café Origen";
    private const string Verify = "buyers.email-verification-requested.v1";

    private sealed class PassingCaptcha : IBuyerCaptcha
    {
        public Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private static Task<PublicStoreFixture> OpenAsync(bool publish = true) =>
        PublicStoreFixture.OpenAsync(
            (_, body) => body["name"] = StoreName,
            publish,
            configureServices: services =>
            {
                services.RemoveAll<IBuyerCaptcha>();
                services.AddSingleton<IBuyerCaptcha>(new PassingCaptcha());
            });

    private static Task<HttpResponseMessage> RegisterAsync(PublicStoreFixture fixture, string host = Host)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url("/buyer/register", host))
        {
            Content = JsonContent.Create(new
            {
                email = "ana@correo.com",
                password = "una-contrasena-larga",
                acceptsDataPolicy = true,
                turnstileToken = "ok",
                profile = (object?)null,
            }),
        };
        request.Headers.Add("X-Qep-Visitor-Ip", "203.0.113.20");
        return fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<List<JsonNode>> OutboxAsync(PublicStoreFixture fixture, string eventName)
    {
        var connectionString = fixture.Settings.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name", connection);
        command.Parameters.AddWithValue("name", eventName);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var payloads = new List<JsonNode>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(JsonNode.Parse(reader.GetString(0))!);
        }

        return payloads;
    }

    [Fact]
    public async Task RegisterOnAPublishedStoreQueuesAnEmailNamingTheStore()
    {
        await using var fixture = await OpenAsync();

        var response = await RegisterAsync(fixture);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = Assert.Single(await OutboxAsync(fixture, Verify));
        Assert.Equal(StoreName, (string?)payload["storeName"]);
        Assert.Equal(Host, (string?)payload["storeHost"]);
        Assert.Equal("ana@correo.com", (string?)payload["email"]);
    }

    [Fact]
    public async Task RegisterOnAnUnpublishedStoreIs404()
    {
        await using var fixture = await OpenAsync(publish: false);

        var response = await RegisterAsync(fixture);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("store.public.not_found", (string?)json["code"]);
        Assert.Empty(await OutboxAsync(fixture, Verify));
    }
}
