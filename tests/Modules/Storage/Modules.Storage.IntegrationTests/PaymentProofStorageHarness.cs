using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Testcontainers.PostgreSql;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de los comprobantes de pago v2 (spec 2026-09-16): la
/// subida, el movimiento al bucket público, la descarga, el barrido de staging y la reconciliación.
/// Aparte de StorageFlowTests porque éstas necesitan los dos buckets en memoria y, desde Task 7,
/// correr a mano los procesadores de los workers.
/// </summary>
internal static class PaymentProofStorageHarness
{
    public static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");

    public static readonly Guid SubjectId = Guid.Parse("01900000-0000-7000-8000-000000000002");

    private const string Permissions = "storage.file.upload,storage.file.read,storage.file.delete";

    public static string FilesUrl { get; } = $"/api/v1/tenants/{TenantId}/files";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public static HttpClient CreateClient(StorageApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", Permissions);
        return client;
    }

    /// <summary>Abre la sesión de subida y simula que R2 acepta el PUT firmado. Devuelve el id y la
    /// clave de staging.</summary>
    public static async Task<UploadedFile> UploadAsync(
        HttpClient client,
        StorageApiFactory factory,
        string ownerType,
        string name,
        string mimeType,
        byte[] payload)
    {
        using var response = await client.PostAsJsonAsync(
            FilesUrl,
            new { ownerId = SubjectId, ownerType, name, mimeType, sizeBytes = payload.Length },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<UploadSessionPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        factory.ObjectStorage.Upload(session.StorageKey, payload);
        return new UploadedFile(session.FileResourceId, session.StorageKey);
    }

    public static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid fileId) =>
        client.PostAsync(
            $"{FilesUrl}/{fileId}/complete", content: null, TestContext.Current.CancellationToken);

    /// <summary>Sube y completa, y exige 200.</summary>
    public static async Task<UploadedFile> CreateAvailableAsync(
        HttpClient client,
        StorageApiFactory factory,
        string ownerType,
        string name,
        string mimeType,
        byte[] payload)
    {
        var uploaded = await UploadAsync(client, factory, ownerType, name, mimeType, payload);
        using var response = await CompleteAsync(client, uploaded.FileId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return uploaded;
    }

    public static async Task<byte[]> PngAsync(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, Color.CornflowerBlue);
        await using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    public static byte[] Pdf() => "%PDF-1.7\ncomprobante"u8.ToArray();

    /// <summary>La fila de storage.file_resources y cuántas variantes tiene, leída con SQL para no
    /// depender de lo que expone la API.</summary>
    public static async Task<FileRow> ReadFileAsync(string connectionString, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT status, owner_type, mime_type, name, size_bytes, storage_key, public_storage_key, checksum,
                   (SELECT count(*) FROM storage.file_variants WHERE file_resource_id = @id)
            FROM storage.file_resources
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", fileId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new FileRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8));
    }
}

internal sealed record UploadedFile(Guid FileId, string StagingKey);

internal sealed record FileRow(
    string Status,
    string OwnerType,
    string MimeType,
    string Name,
    long SizeBytes,
    string StorageKey,
    string? PublicStorageKey,
    string? Checksum,
    long VariantCount);

internal sealed record UploadSessionPayload(Guid FileResourceId, string UploadUrl, string StorageKey);

internal sealed record FilePayload(
    Guid Id,
    string OwnerType,
    string Name,
    string MimeType,
    long SizeBytes,
    string Status,
    IReadOnlyList<FileVariantPayload> Variants);

internal sealed record FileVariantPayload(string Name);

internal sealed record ProblemPayload(string? Code);

/// <summary>El host de la API con los dos buckets en memoria.</summary>
internal sealed class StorageApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public InMemoryObjectStorage ObjectStorage { get; } = new();

    public InMemoryPublicObjectStorage PublicObjectStorage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
        builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
        builder.UseSetting("Storage:R2:AccountId", "test-account");
        builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
        builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
        builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        // Fijados, nunca heredados de appsettings.json ni de los user-secrets de quien corre las
        // pruebas: mismo criterio que StorageFlowTests.
        builder.UseSetting("Notifications:EmailProvider", "log");
        builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage>(ObjectStorage);
            services.RemoveAll<IPublicObjectStorage>();
            services.AddSingleton<IPublicObjectStorage>(PublicObjectStorage);
        });
    }
}

/// <summary>El bucket privado en memoria. Concurrente porque los hosted services del host corren en
/// otros hilos mientras la prueba lee.</summary>
internal sealed class InMemoryObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    /// <summary>La clave cuyo borrado falla, para ejercer el orden de D9 (Task 7); null si ninguna.</summary>
    public string? FailingDeleteKey { get; set; }

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToArray();

    public Task<Uri> CreatePresignedUploadUrlAsync(
        string key, string contentType, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://r2.test/{key}"));

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<StoredObject?>(_objects.TryGetValue(key, out var content)
            ? new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)))
            : null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (string.Equals(key, FailingDeleteKey, StringComparison.Ordinal))
        {
            return Task.FromException(
                new InvalidOperationException("Simulated failure deleting from the private bucket."));
        }

        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task PromoteAsync(
        string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
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

    public void Upload(string key, byte[] content) => _objects[key] = content.ToArray();

    public byte[] Read(string key) => _objects[key].ToArray();

    public bool Exists(string key) => _objects.ContainsKey(key);

    public void Remove(string key) => _objects.TryRemove(key, out _);
}

/// <summary>El bucket público en memoria, con fecha de modificación por objeto y paginación forzada
/// de a <see cref="PageSize"/>. El token de continuación es la última clave de la página, como el de
/// S3: borrar lo ya listado no corre las páginas siguientes.</summary>
internal sealed class InMemoryPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets.qep.test";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _objects = new(StringComparer.Ordinal);

    public int PageSize { get; set; } = 2;

    public List<string> ListedPrefixes { get; } = [];

    public List<string> DeletedKeys { get; } = [];

    public bool IsConfigured => true;

    public void Put(string key, DateTimeOffset lastModified) => _objects[key] = lastModified;

    public bool Exists(string key) => _objects.ContainsKey(key);

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
    {
        _objects[publicKey] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        _objects.TryRemove(publicKey, out _);
        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken)
    {
        ListedPrefixes.Add(prefix);
        var remaining = _objects
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(entry => continuationToken is null
                || string.CompareOrdinal(entry.Key, continuationToken) > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var page = remaining
            .Take(PageSize)
            .Select(entry => new PublicStoredObject(entry.Key, entry.Value))
            .ToArray();
        var next = remaining.Length > PageSize ? page[^1].Key : null;
        return Task.FromResult(new PublicObjectPage(page, next));
    }
}
