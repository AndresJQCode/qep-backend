using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.6: copia con un almacén falso (sha256 verificado; el que no coincide falla y
/// reintenta), 404 sin código antes de copiar, headers de seguridad al servir, Review Focus 5 (HTML/SVG
/// como attachment), y el medio de otro tenant es 404.</summary>
public sealed class MediaApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>El almacén de prueba: un diccionario, para no tocar R2.</summary>
    private sealed class FakeMediaStore : IMessagingMediaStore
    {
        public Dictionary<string, (byte[] Bytes, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = (buffer.ToArray(), contentType);
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Objects.TryGetValue(key, out var stored) ? new MediaStreamDto(new MemoryStream(stored.Bytes), stored.ContentType, stored.Bytes.Length, null) : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeMediaStore Store, string ConnectionString, RegisteredTenant Tenant, Guid MessageId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(
        TestDatabase database, string mimeType = "image/jpeg", string? fileName = null, byte[]? bytes = null, bool wrongSha = false, string mediaId = "media-1")
    {
        bytes ??= Encoding.UTF8.GetBytes("imagen de prueba");
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var store = new FakeMediaStore();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(store)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        factory.MetaHandler.Respond("/media-1", HttpStatusCode.OK, $$"""{"url":"https://lookaside.test/m/1","mime_type":"{{mimeType}}","sha256":"{{(wrongSha ? "deadbeef" : sha)}}","file_size":{{bytes.Length}},"id":"media-1"}""");
        // El fake compara contra el path: la URL firmada de Meta es https://lookaside.test/m/1.
        factory.MetaHandler.RespondWith("^/m/1$", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var anonymous = host.CreateClient();
        // Hora real: la copia se rinde pasados 7 días desde occurred_at (§8.6).
        var occurredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573001234567", "w1", occurredAt, mimeType.StartsWith("image/", StringComparison.Ordinal) ? "image" : "document", mediaId, mimeType, caption: "leyenda", filename: fileName));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var messageId = (await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, host, store, connectionString, tenant, messageId, client);
    }

    [Fact]
    public async Task BeforeTheCopyItIs404WithoutCodeAndAfterItIsServedWithSecurityHeaders()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;

        var pending = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
        Assert.Empty(await pending.Content.ReadAsStringAsync(Ct));

        await DrainMediaAsync(f.Host);
        var served = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/jpeg", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(16, served.Content.Headers.ContentLength);
        Assert.Equal("inline", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox; default-src 'none'", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("private, max-age=3600", served.Headers.NonValidated["Cache-Control"].ToString());
        Assert.Equal("imagen de prueba", await served.Content.ReadAsStringAsync(Ct));
        Assert.Equal($"messaging/{f.Tenant.TenantId}/{f.MessageId}", Assert.Single(f.Store.Objects.Keys));
        Assert.Equal("true|16", await ScalarAsync<string>(f.ConnectionString, "SELECT (stored_at IS NOT NULL)::text || '|' || size_bytes FROM messaging.message_media"));
        var download = Assert.Single(f.Factory.MetaHandler.Requests, request => request.Uri!.Host == "lookaside.test");
        Assert.Equal($"Bearer {SentinelMetaAccessToken}", download.Authorization);
    }

    // Review Focus 5.
    [Theory]
    [InlineData("text/html", "page.html")]
    [InlineData("image/svg+xml", "logo.svg")]
    [InlineData("application/pdf", "orden.pdf")]
    public async Task AnHtmlOrSvgMediaIsServedAsAttachmentWithSandbox(string mimeType, string fileName)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, mimeType, fileName, Encoding.UTF8.GetBytes("<svg onload=alert(1)/>"));
        using var _ = f.Factory;
        using var __ = f.Host;
        await DrainMediaAsync(f.Host);

        var served = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("attachment", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains(fileName, served.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        Assert.Equal("sandbox; default-src 'none'", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task AShaMismatchFailsTheAttemptAndSchedulesARetry()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, wrongSha: true);
        using var _ = f.Factory;
        using var __ = f.Host;

        await DrainMediaAsync(f.Host);

        Assert.Empty(f.Store.Objects);
        Assert.Equal("false|1|sha256_mismatch", await ScalarAsync<string>(f.ConnectionString, "SELECT (stored_at IS NOT NULL)::text || '|' || attempts || '|' || last_error FROM messaging.message_media"));
        Assert.True(await ScalarAsync<bool>(f.ConnectionString, "SELECT next_attempt_at > now() FROM messaging.message_media"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct)).StatusCode);
    }

    [Fact]
    public async Task AnotherTenantAndAMessageWithoutMediaAre404WithoutCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        await DrainMediaAsync(f.Host);
        var other = await RegisterTenantAsync(f.Host);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Host, other.OwnerUserId, other.TenantId, ReadPermissions);

        var foreign = await otherClient.GetAsync(MediaUrl(other.TenantId, f.MessageId), Ct);
        var crossTenant = await otherClient.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct);
        var random = await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(await foreign.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, random.StatusCode);
    }

    // Arrastre de la Task 13: un id de Meta más largo que la columna (varchar 64) hacía fallar el INSERT y
    // con él el mensaje entero. Ahora el mensaje entra, el medio queda rendido con last_error y nunca se pide.
    [Fact]
    public async Task AnOverlongMediaIdKeepsTheMessageAndGivesUpOnTheMedia()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, mediaId: new string('9', 65));
        using var _ = f.Factory;
        using var __ = f.Host;

        await DrainMediaAsync(f.Host);

        Assert.Empty(f.Store.Objects);
        Assert.Equal("false|0|invalid_media_id", await ScalarAsync<string>(f.ConnectionString, "SELECT (stored_at IS NOT NULL)::text || '|' || attempts || '|' || last_error FROM messaging.message_media"));
        Assert.DoesNotContain(f.Factory.MetaHandler.Requests, request => request.Uri!.AbsolutePath.EndsWith("99999", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync(MediaUrl(f.Tenant.TenantId, f.MessageId), Ct)).StatusCode);
    }
}
