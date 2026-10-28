using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Messaging-media-reply R1, R8 y R17 por HTTP: la sesión de subida responde 200 con la URL y las cabeceras que firmó,
/// 403 (nunca 404) sin permiso o desde otro tenant, y 422 con el mapa <c>errors</c> o con el código de dominio. El staging se
/// reemplaza por un doble: firmar contra R2 pide credenciales que el harness no tiene.</summary>
public sealed class MediaUploadSessionApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeStaging : IMessagingUploadStaging
    {
        public List<string> Keys { get; } = [];

        public Task<MessagingUploadUrl> CreateUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken)
        {
            Keys.Add(key);
            return Task.FromResult(new MessagingUploadUrl(new Uri($"https://r2.test/{key}"), DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<StagedUpload?> StatAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromoteAsync(string stagingKey, string finalKey, string eTag, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record Fixture(QepApiFactory Factory, WebApplicationFactory<Program> Host, FakeStaging Staging, string ConnectionString, RegisteredTenant Tenant, Guid ConversationId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var staging = new FakeStaging();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingUploadStaging>(staging)));
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = host.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, "hola"));
        await DrainDeliveriesAsync(host);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new Fixture(factory, host, staging, connectionString, tenant, conversationId, client);
    }

    private static string SessionUrl(Guid tenantId, Guid conversationId) => MessagesUrl(tenantId, conversationId) + "/media/upload-session";

    [Fact]
    public async Task AValidRequestAnswers200WithTheSignedUploadAndItsHeaders()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;

        var response = await SendAsync(f.Client, HttpMethod.Post, SessionUrl(f.Tenant.TenantId, f.ConversationId), new { fileName = "photo.jpg", mimeType = "image/jpeg", sizeBytes = 3_000_000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var uploadId = body.GetProperty("uploadId").GetGuid();
        Assert.NotEqual(Guid.Empty, uploadId);
        Assert.StartsWith("https://r2.test/messaging/staging/", body.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Equal("image/jpeg", body.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.Equal("*", body.GetProperty("headers").GetProperty("If-None-Match").GetString());
        var key = Assert.Single(f.Staging.Keys);
        Assert.Contains($"/{f.Tenant.TenantId}/{f.ConversationId}/", key, StringComparison.Ordinal);
        Assert.EndsWith($"{uploadId}.jpg", key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallerNeedsThePermissionAndTheTenantOfTheRoute()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var url = SessionUrl(f.Tenant.TenantId, f.ConversationId);
        var body = new { fileName = "a.pdf", mimeType = "application/pdf", sizeBytes = 1000 };
        var other = await RegisterTenantAsync(f.Host);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Host, other.OwnerUserId, other.TenantId, ManagePermissions);
        using var reader = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var cross = await SendAsync(otherClient, HttpMethod.Post, url, body);
        var readOnly = await SendAsync(reader, HttpMethod.Post, url, body);

        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Empty(f.Staging.Keys);
    }

    [Fact]
    public async Task ABadBodyIs422WithTheFieldMapAndABadTypeIs422WithTheDomainCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var __ = f.Host;
        var url = SessionUrl(f.Tenant.TenantId, f.ConversationId);

        var shape = await SendAsync(f.Client, HttpMethod.Post, url, new { fileName = "", mimeType = "image/jpeg", sizeBytes = 0 });
        var webp = await SendAsync(f.Client, HttpMethod.Post, url, new { fileName = "a.webp", mimeType = "image/webp", sizeBytes = 1000 });
        var big = await SendAsync(f.Client, HttpMethod.Post, url, new { fileName = "a.jpg", mimeType = "image/jpeg", sizeBytes = 26L * 1024 * 1024 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, shape.StatusCode);
        var problem = await ProblemAsync(shape);
        Assert.Equal("validation.failed", problem.Code);
        Assert.Equal(["fileName", "sizeBytes"], problem.ErrorKeys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, webp.StatusCode);
        Assert.Equal("messaging.media.unsupported_format", (await ProblemAsync(webp)).Code);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, big.StatusCode);
        Assert.Equal("messaging.media.too_large", (await ProblemAsync(big)).Code);
        Assert.Empty(f.Staging.Keys);
    }
}
