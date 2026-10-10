using System.Net;
using System.Text;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2: GET con token bueno y malo; POST firmado → 200 y una fila; sin firma o
/// mal firmado → 401 y cero filas; cuerpo sobre el tope → 413; reenvío idéntico → una sola fila.</summary>
public sealed class WebhookApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheVerificationAnswersTheChallengeOnlyWithTheRightToken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var ok = await client.GetAsync($"{WebhookUrl}?hub.mode=subscribe&hub.verify_token={Uri.EscapeDataString(TestVerifyToken)}&hub.challenge=123456", Ct);
        var wrong = await client.GetAsync($"{WebhookUrl}?hub.mode=subscribe&hub.verify_token=nope&hub.challenge=1", Ct);
        var wrongMode = await client.GetAsync($"{WebhookUrl}?hub.mode=unsubscribe&hub.verify_token={Uri.EscapeDataString(TestVerifyToken)}&hub.challenge=1", Ct);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("123456", await ok.Content.ReadAsStringAsync(Ct));
        Assert.Equal("text/plain", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", ok.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongMode.StatusCode);
    }

    [Fact]
    public async Task ASignedPostIsStoredOnceAndAnsweredEmpty()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();
        var json = MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "hola");

        var first = await PostWebhookAsync(client, json);
        var second = await PostWebhookAsync(client, json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Empty(await first.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NULL AND attempts = 0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256=zz")]
    public async Task AMissingOrInvalidSignatureIs401WithoutTouchingTheDatabase(string? signature)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();

        var response = await PostWebhookAsync(client, MetaPayloads.InboundText("111", "573001234567", "wamid.1", 1760000000, "hola"), signature ?? "");
        if (signature is null)
        {
            // Sin header en absoluto.
            using var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response = await client.SendAsync(request, Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
    }

    [Fact]
    public async Task ABodyOverTheCapIs413()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var host = factory.WithWebHostBuilder(builder => builder.UseSetting("Messaging:Webhook:MaxBodyBytes", "1024"));
        using var client = host.CreateClient();
        var json = "{\"pad\":\"" + new string('x', 2048) + "\"}";

        var response = await PostWebhookAsync(client, json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries"));
    }

    // Sin Meta:App (D-M3, sólo fuera de producción): 403 al GET y 401 al POST.
    [Fact]
    public async Task WithoutMetaAppTheWebhookRejectsEverything()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Meta:App:AppSecret", string.Empty);
            builder.UseSetting("Meta:App:WebhookVerifyToken", string.Empty);
        });
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{WebhookUrl}?hub.mode=subscribe&hub.verify_token=&hub.challenge=1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync(client, "{}", Sign("{}"u8.ToArray()))).StatusCode);
    }
}
