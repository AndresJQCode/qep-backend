using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Storage.IntegrationTests;

public sealed class StorageFlowTests
{
    private const string SeededTenantId = "01900000-0000-7000-8000-000000000001";
    private const string AdminSubjectId = "01900000-0000-7000-8000-000000000002";
    private const string StoragePermissions =
        "storage.file.upload,storage.file.read,storage.file.delete";
    private static readonly string[] RequestedMetadataTags = ["Legal", "Cliente VIP"];
    private static readonly string[] ExpectedMetadataTags = ["legal", "cliente vip"];

    [Fact]
    public async Task UploadScanDownloadDeleteFlowWithObjectStorageDoubleAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);

        var payload = Encoding.UTF8.GetBytes("%PDF-1.7\nhello storage");

        // 1. Crear la sesión de subida (el recurso queda en PendingUpload).
        var session = await CreateSessionAsync(client, payload.Length);
        Assert.NotEqual(Guid.Empty, session.FileResourceId);
        Assert.StartsWith("https://r2.test/", session.UploadUrl, StringComparison.Ordinal);

        // 2. Simular que R2 acepta los bytes por su URL prefirmada.
        factory.ObjectStorage.Upload(session.StorageKey, payload);

        // 3. Completar la subida: verificada, escaneada (no-op limpio), pasa a Available.
        var completed = await CompleteAsync(client, session.FileResourceId);
        Assert.Equal("Available", completed.Status);

        // 4. Emitir una URL de descarga recién cuando el recurso queda Available.
        var download = await IssueDownloadAsync(client, session.FileResourceId);
        Assert.StartsWith("https://r2.test/", download.Url, StringComparison.Ordinal);
        var finalKey = new Uri(download.Url).AbsolutePath.TrimStart('/');
        Assert.StartsWith("files/tenants/", finalKey, StringComparison.Ordinal);
        Assert.Equal(payload, factory.ObjectStorage.Read(finalKey));
        Assert.False(factory.ObjectStorage.Exists(session.StorageKey));

        var listed = await client.GetFromJsonAsync<PagedFilesPayload>(
            $"/api/v1/tenants/{SeededTenantId}/files",
            TestContext.Current.CancellationToken);
        Assert.NotNull(listed);
        Assert.Single(listed.Items);
        Assert.Equal(session.FileResourceId, listed.Items[0].Id);

        var metadataResponse = await client.PatchAsJsonAsync(
            $"/api/v1/tenants/{SeededTenantId}/files/{session.FileResourceId}/metadata",
            new { category = "Contratos", tags = RequestedMetadataTags },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<FileMetadataPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal("Contratos", metadata.Category);
        Assert.Equal(ExpectedMetadataTags, metadata.Tags);

        var filtered = await client.GetFromJsonAsync<PagedFilesPayload>(
            $"/api/v1/tenants/{SeededTenantId}/files?category=Contratos&tag=legal",
            TestContext.Current.CancellationToken);
        Assert.NotNull(filtered);
        Assert.Single(filtered.Items);

        // 5. La auditoría operativa aterriza en audit.entries por el worker de proyección.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var auditCount = await WaitForAuditAsync(connection, session.FileResourceId.ToString());
        Assert.Equal(1L, auditCount);

        // 6. Borrado lógico; el recurso deja de ser descargable.
        var deleted = await DeleteAsync(client, session.FileResourceId);
        Assert.True(deleted.Deleted);

        var afterDelete = await client.PostAsync(
            $"/api/v1/tenants/{SeededTenantId}/files/{session.FileResourceId}/download-url",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, afterDelete.StatusCode);
    }

    [Fact]
    public async Task CompleteBeforeUploadReportsPreconditionRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);

        var session = await CreateSessionAsync(client, sizeBytes: 16);
        // Sin PUT: el objeto nunca se subió.
        var complete = await client.PostAsync(
            $"/api/v1/tenants/{SeededTenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionRequired, complete.StatusCode);
    }

    private static async Task<UploadSessionPayload> CreateSessionAsync(HttpClient client, int sizeBytes)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{SeededTenantId}/files",
            new
            {
                ownerId = Guid.Parse(AdminSubjectId),
                ownerType = "User",
                name = "greeting.pdf",
                mimeType = "application/pdf",
                sizeBytes,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadSessionPayload>(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<FileResourcePayload> CompleteAsync(HttpClient client, Guid fileId)
    {
        var response = await client.PostAsync(
            $"/api/v1/tenants/{SeededTenantId}/files/{fileId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileResourcePayload>(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<DownloadUrlPayload> IssueDownloadAsync(HttpClient client, Guid fileId)
    {
        var response = await client.PostAsync(
            $"/api/v1/tenants/{SeededTenantId}/files/{fileId}/download-url",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DownloadUrlPayload>(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<SoftDeletePayload> DeleteAsync(HttpClient client, Guid fileId)
    {
        var response = await client.DeleteAsync(
            $"/api/v1/tenants/{SeededTenantId}/files/{fileId}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SoftDeletePayload>(
            TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> WaitForAuditAsync(NpgsqlConnection connection, string resourceId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM audit.entries
                WHERE action = 'storage.file.uploaded' AND resource_id = @resourceId
                """,
                connection);
            command.Parameters.AddWithValue("resourceId", resourceId);
            var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            if (Convert.ToInt64(result, CultureInfo.InvariantCulture) >= 1)
            {
                return 1;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        }

        return 0;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", AdminSubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", SeededTenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", StoragePermissions);
        return client;
    }

    private sealed record UploadSessionPayload(Guid FileResourceId, string UploadUrl, string StorageKey);

    private sealed record FileResourcePayload(Guid Id, string Status);

    private sealed record FileMetadataPayload(Guid Id, string? Category, string[] Tags);

    private sealed record DownloadUrlPayload(string Url);

    private sealed record SoftDeletePayload(bool Deleted);

    private sealed record PagedFilesPayload(IReadOnlyList<FileResourcePayload> Items, int TotalCount);

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        public InMemoryObjectStorage ObjectStorage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17: sin esto hereda
            // "infobip" y, sin sus claves, NotificationsOptionsValidator tira al arrancar
            // antes de que la prueba llegue a su aserción — enmascarado en local por los
            // user-secrets de Infobip de cada developer, pero no en CI.
            builder.UseSetting("Notifications:EmailProvider", "log");
            AddR2TestSettings(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage>(ObjectStorage);
            });
        }
    }

    private static void AddR2TestSettings(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:R2:AccountId", "test-account");
        builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
        builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
        builder.UseSetting("Storage:R2:Bucket", "test-bucket");
    }

    private sealed class InMemoryObjectStorage : IObjectStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key,
            string contentType,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key,
            TimeSpan expiry,
            string? downloadFileName,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<StoredObject?> StatAsync(
            string key,
            CancellationToken cancellationToken)
        {
            if (!_objects.TryGetValue(key, out var content))
            {
                return Task.FromResult<StoredObject?>(null);
            }

            var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
            return Task.FromResult<StoredObject?>(
                new StoredObject(content.LongLength, checksum));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task PromoteAsync(
            string sourceKey,
            string destinationKey,
            string expectedChecksum,
            CancellationToken cancellationToken)
        {
            _objects[destinationKey] = _objects[sourceKey].ToArray();
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_objects[key].ToArray());

        public Task UploadAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            _objects[key] = content.ToArray();
            return Task.CompletedTask;
        }

        public void Upload(string key, byte[] content) =>
            _objects[key] = content.ToArray();

        public byte[] Read(string key) => _objects[key].ToArray();

        public bool Exists(string key) => _objects.ContainsKey(key);
    }
}
