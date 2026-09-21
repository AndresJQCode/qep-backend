using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// PUT/DELETE /settings/logo de punta a punta (spec 2026-09-19): sube el archivo por el pipeline
/// de Storage, lo asigna, y confirma que `GET /settings` refleja la URL pública, la auditoría y el
/// outbox. Factory propia (no `TenantSettingsApiTests.QepApiFactory`) porque sustituye
/// `IObjectStorage`/`IPublicObjectStorage` por dobles en memoria — sin eso, el `PUT` real
/// intentaría hablarle a R2.
/// </summary>
public sealed class TenantLogoApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string StoragePermissions =
        "storage.file.upload,tenancy.settings.read,tenancy.settings.update";

    private static readonly JsonSerializerOptions CaseInsensitiveJson =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task UploadCompleteAndAssignShowsTheLogoUrlInSettings()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);

        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        var settings = await response.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(settings!.Logo);
        Assert.Equal(fileId, settings.Logo!.FileId);
        Assert.StartsWith(
            InMemoryPublicObjectStorage.BaseUrl, settings.Logo.Url, StringComparison.Ordinal);

        var getResponse = await client.GetAsync(SettingsUrl, TestContext.Current.CancellationToken);
        // Leído una sola vez como texto: el HttpContent no es releíble, y de acá salen tanto el
        // deserializado tipado como el JSON crudo para pinear el contrato de cable.
        var getJson = await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var getSettings = JsonSerializer.Deserialize<SettingsPayload>(getJson, CaseInsensitiveJson);
        Assert.Equal(settings.Logo.Url, getSettings!.Logo!.Url);

        // JsonSerializer con PropertyNameCaseInsensitive (igual que ReadFromJsonAsync por
        // defecto) no habría detectado una regresión a PascalCase (`Logo.FileId` en vez de
        // `logo.fileId`) — se ata el contrato de cable leyendo las claves crudas.
        using var document = JsonDocument.Parse(getJson);
        var logoElement = document.RootElement.GetProperty("logo");
        Assert.Equal(fileId, logoElement.GetProperty("fileId").GetGuid());
        Assert.StartsWith(
            InMemoryPublicObjectStorage.BaseUrl,
            logoElement.GetProperty("url").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplacingTheLogoUnpublishesAndDeletesTheOldFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var oldFileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var firstEtag = await GetEtagAsync(client);
        var firstPut = await PutLogoAsync(client, firstEtag, oldFileId);
        var secondEtag = firstPut.Headers.ETag!.Tag;
        // Hay que capturar la clave pública ANTES del reemplazo: TenantLogoStorage.UnpublishAsync
        // llama a FileResource.SoftDelete, que a su vez deja PublicStorageKey en null
        // (FileResource.Unpublish) — después del reemplazo ya no queda forma de leerla del archivo.
        var oldPublicKey = await FileOwnPublicKeyAsync(factory, oldFileId);

        var newFileId = await UploadTenantFileAsync(client, factory, "image/webp", 2048);
        var secondPut = await PutLogoAsync(client, secondEtag, newFileId);

        Assert.Equal(HttpStatusCode.OK, secondPut.StatusCode);
        Assert.Contains(oldPublicKey, factory.PublicObjectStorage.DeletedKeys);
        var status = await FileStatusAsync(factory, oldFileId);
        Assert.NotEqual("Available", status);
    }

    [Fact]
    public async Task RemovingTheLogoLeavesSettingsWithoutLogo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);
        var afterPut = await PutLogoAsync(client, etag, fileId);
        var putEtag = afterPut.Headers.ETag!.Tag;
        // Capturada ANTES del DELETE por la misma razón que en ReplacingTheLogoUnpublishesAndDeletesTheOldFile:
        // FileResource.Unpublish deja PublicStorageKey en null en cuanto se retira.
        var publicKey = await FileOwnPublicKeyAsync(factory, fileId);

        var response = await DeleteLogoAsync(client, putEtag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(putEtag, response.Headers.ETag!.Tag);
        var settings = await response.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(settings!.Logo);
        // Spec: "logo == null, copia borrada, Version sube" — las dos primeras partes no las
        // probaba ninguna prueba todavía.
        Assert.Contains(publicKey, factory.PublicObjectStorage.DeletedKeys);
        var status = await FileStatusAsync(factory, fileId);
        Assert.NotEqual("Available", status);
    }

    [Fact]
    public async Task DeleteMissingIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SettingsUrl}/logo");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem?.Code);
    }

    [Fact]
    public async Task DeleteStaleIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);
        var putResponse = await PutLogoAsync(client, etag, fileId);
        var staleEtag = putResponse.Headers.ETag!.Tag;
        await UpdateDisplayNameAsync(client, staleEtag);

        var response = await DeleteLogoAsync(client, staleEtag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("concurrency.conflict", problem?.Code);
    }

    [Fact]
    public async Task DeletingWithNoLogoReturnsOkWithUnchangedVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var etag = await GetEtagAsync(client);

        // RemoveTenantLogoHandler corta temprano cuando tenant.LogoFileId ya es null
        // (RemoveTenantLogo.cs): sin logo que quitar no hay evento ni versión nueva.
        var response = await DeleteLogoAsync(client, etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.Tag);
        var settings = await response.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(settings!.Logo);
    }

    [Fact]
    public async Task RemovingWritesAuditEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);
        var putResponse = await PutLogoAsync(client, etag, fileId);
        var putEtag = putResponse.Headers.ETag!.Tag;

        var response = await DeleteLogoAsync(client, putEtag);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var audit = await QueryRowAsync(
            connection,
            """
            SELECT outcome FROM audit.entries
            WHERE tenant_id = @tenantId AND action = 'tenancy.logo.removed'
            ORDER BY occurred_at DESC LIMIT 1
            """,
            TenantId);
        Assert.NotNull(audit);
        Assert.Equal("success", audit![0]);
    }

    [Fact]
    public async Task AFileOfAnotherTenantIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        using var otherClient = factory.CreateClient();
        otherClient.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        otherClient.DefaultRequestHeaders.Add("X-Tenant-Id", OtherTenantId);
        otherClient.DefaultRequestHeaders.Add("X-Permissions", "storage.file.upload");
        var otherTenantFileId = await UploadTenantFileAsync(
            otherClient, factory, "image/png", 2048, tenantId: OtherTenantId);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, otherTenantFileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.logo.file_not_found", problem?.Code);
    }

    [Fact]
    public async Task ANonImageIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "application/pdf", 2048);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.logo.not_image", problem?.Code);
    }

    [Fact]
    public async Task AFileOverTwoMebibytesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", (2 * 1024 * 1024) + 1);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.logo.too_large", problem?.Code);
    }

    [Fact]
    public async Task StaleIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var staleEtag = await GetEtagAsync(client);
        await UpdateDisplayNameAsync(client, staleEtag);

        var response = await PutLogoAsync(client, staleEtag, fileId);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("concurrency.conflict", problem?.Code);
        // Decisión 7 del spec: la falla de concurrencia se detecta antes de tocar Storage. El
        // bucket público en memoria del double queda vacío — nada quedó publicado por el intento
        // fallido.
        Assert.Equal(0, factory.PublicObjectStorage.Count);
        // Ídem, pero probando que nunca se creó una copia (y se retiró) en vez de que nunca se
        // haya intentado retirar nada — DeletedKeys vacío descarta el camino "publicó y hizo
        // rollback" que Count por sí solo no distingue.
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        var status = await FileStatusAsync(factory, fileId);
        Assert.Equal("Available", status);
    }

    [Fact]
    public async Task MissingIfMatchIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"{SettingsUrl}/logo")
        {
            Content = JsonContent.Create(new SetTenantLogoRequestPayload(fileId)),
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem?.Code);
    }

    [Fact]
    public async Task AssigningWritesAuditAndOutbox()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);

        var response = await PutLogoAsync(client, etag, fileId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var audit = await QueryRowAsync(
            connection,
            """
            SELECT outcome FROM audit.entries
            WHERE tenant_id = @tenantId AND action = 'tenancy.logo.updated'
            ORDER BY occurred_at DESC LIMIT 1
            """,
            TenantId);
        Assert.NotNull(audit);
        Assert.Equal("success", audit![0]);

        var outbox = await QueryRowAsync(
            connection,
            """
            SELECT event_name FROM platform.outbox_messages
            WHERE event_name = 'tenancy.tenant-logo-updated.v1'
            ORDER BY occurred_at DESC LIMIT 1
            """);
        Assert.NotNull(outbox);
    }

    [Fact]
    public async Task AssigningTheSameFileIdAgainReturnsOkWithoutBumpingTheVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new TenantLogoApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var fileId = await UploadTenantFileAsync(client, factory, "image/png", 2048);
        var etag = await GetEtagAsync(client);
        var firstPut = await PutLogoAsync(client, etag, fileId);
        var firstEtag = firstPut.Headers.ETag!.Tag;

        // Reasignar el mismo fileId es una operación idempotente para el handler (SetTenantLogo.cs):
        // no toca Storage ni el agregado, así que la versión no cambia — pero igual exige un
        // If-Match válido, porque el endpoint no distingue este caso antes del dispatch.
        var secondPut = await PutLogoAsync(client, firstEtag, fileId);

        Assert.Equal(HttpStatusCode.OK, secondPut.StatusCode);
        Assert.Equal(firstEtag, secondPut.Headers.ETag!.Tag);
        var settings = await secondPut.Content.ReadFromJsonAsync<SettingsPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(fileId, settings!.Logo!.FileId);
    }

    private static string SettingsUrl => $"/api/v1/tenants/{TenantId}/settings";

    private static async Task<Guid> UploadTenantFileAsync(
        HttpClient client, TenantLogoApiFactory factory, string mimeType, int minimumSizeBytes,
        string tenantId = TenantId)
    {
        var content = await BuildSignedContentAsync(mimeType, minimumSizeBytes);

        var sessionResponse = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/files",
            new
            {
                ownerId = Guid.Parse(tenantId),
                ownerType = "Tenant",
                name = "logo" + ExtensionFor(mimeType),
                mimeType,
                // Declarado = lo que realmente se subió, no el pedido: CompleteUploadHandler
                // rechaza con storage.file.size_invalid si no coinciden byte a byte.
                sizeBytes = content.Length,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = (await sessionResponse.Content.ReadFromJsonAsync<UploadSessionPayload>(
            TestContext.Current.CancellationToken))!;

        factory.ObjectStorage.Upload(session.StorageKey, content);

        var completeResponse = await client.PostAsync(
            $"/api/v1/tenants/{tenantId}/files/{session.FileResourceId}/complete",
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        return session.FileResourceId;
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "application/pdf" => ".pdf",
        "image/webp" => ".webp",
        "image/jpeg" => ".jpg",
        _ => ".png",
    };

    /// <summary>`CompleteUploadHandler` decodifica cualquier `image/*` con ImageSharp para armar la
    /// miniatura (`ImageSharpVariantGenerator.cs`): un PNG/WEBP de mentiritas —sólo el número
    /// mágico— llega quarantined con `storage.image.invalid`, no `Available`. Se genera una imagen
    /// real, con ruido pseudoaleatorio de semilla fija (determinista, mismo criterio que
    /// `ImageSharpPaymentProofImageProcessorTests.PngAsync`) para que ni PNG ni WEBP la compriman
    /// por debajo de `minimumSizeBytes` — lo que ejercita el caso de "demasiado grande". El PDF no
    /// pasa por ImageSharp (`ImageSharpVariantGenerator.Supports` es sólo para `image/*`), así que
    /// le alcanza con el número mágico y relleno.</summary>
    private static async Task<byte[]> BuildSignedContentAsync(string mimeType, int minimumSizeBytes)
    {
        if (mimeType == "application/pdf")
        {
            var content = new byte[Math.Max(minimumSizeBytes, 5)];
            "%PDF-"u8.CopyTo(content);
            return content;
        }

        var side = Math.Max(4, (int)Math.Ceiling(Math.Sqrt(minimumSizeBytes * 1.5 / 4)) + 1);
        using var image = new Image<Rgba32>(side, side);
        var random = new Random(1337);
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            }
        }

        await using var output = new MemoryStream();
        if (mimeType == "image/webp")
        {
            await image.SaveAsWebpAsync(
                output,
                new WebpEncoder { FileFormat = WebpFileFormatType.Lossless },
                TestContext.Current.CancellationToken);
        }
        else
        {
            await image.SaveAsPngAsync(output, TestContext.Current.CancellationToken);
        }

        return output.ToArray();
    }

    private static async Task<HttpResponseMessage> PutLogoAsync(HttpClient client, string ifMatch, Guid fileId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{SettingsUrl}/logo")
        {
            Content = JsonContent.Create(new SetTenantLogoRequestPayload(fileId)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> DeleteLogoAsync(HttpClient client, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SettingsUrl}/logo");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task UpdateDisplayNameAsync(HttpClient client, string ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, SettingsUrl)
        {
            Content = JsonContent.Create(new
            {
                displayName = $"QCode {Guid.NewGuid():N}"[..24],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "dd/MM/yyyy",
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> GetEtagAsync(HttpClient client)
    {
        var response = await client.GetAsync(SettingsUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static async Task<string> FileOwnPublicKeyAsync(TenantLogoApiFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var resource = await repository.GetAsync(
            new Modules.Storage.Domain.FileResourceId(fileId), TestContext.Current.CancellationToken);
        return resource!.PublicStorageKey!;
    }

    private static async Task<string> FileStatusAsync(TenantLogoApiFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFileResourceRepository>();
        var resource = await repository.GetAsync(
            new Modules.Storage.Domain.FileResourceId(fileId), TestContext.Current.CancellationToken);
        return resource!.Status.ToString();
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection, string sql, string? tenantId = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (tenantId is not null)
        {
            command.Parameters.AddWithValue("tenantId", Guid.Parse(tenantId));
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
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

    private static HttpClient CreateClient(TenantLogoApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add("X-Permissions", StoragePermissions);
        return client;
    }

    private sealed record UploadSessionPayload(Guid FileResourceId, string UploadUrl, string StorageKey);

    private sealed record SetTenantLogoRequestPayload(Guid FileId);

    private sealed record TenantLogoPayload(Guid FileId, string? Url);

    private sealed record SettingsPayload(
        Guid TenantId, string DisplayName, long Version, TenantLogoPayload? Logo);

    private sealed record ProblemPayload(string? Code);

    /// <summary>El host con los dos buckets de Storage en memoria — mismo patrón que
    /// `StorageApiFactory` (`PaymentProofStorageHarness.cs:384-426`).</summary>
    private sealed class TenantLogoApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
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

    /// <summary>Recortado de `PaymentProofStorageHarness.InMemoryObjectStorage`: concurrente
    /// porque los workers de Storage (staging cleanup) corren en el host mientras la prueba
    /// sube y lee.</summary>
    internal sealed class InMemoryObjectStorage : IObjectStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, string? downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}"));

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken)
        {
            if (!_objects.TryGetValue(key, out var content))
            {
                return Task.FromResult<StoredObject?>(null);
            }

            return Task.FromResult<StoredObject?>(
                new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content))));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
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
    }

    /// <summary>Recortado de `PaymentProofStorageHarness.InMemoryPublicObjectStorage`.</summary>
    internal sealed class InMemoryPublicObjectStorage : IPublicObjectStorage
    {
        public const string BaseUrl = "https://assets.qep.test";

        private readonly ConcurrentDictionary<string, byte> _objects = new(StringComparer.Ordinal);

        public List<string> DeletedKeys { get; } = [];

        /// <summary>Cuántas copias públicas quedan vivas — para probar que un 412 no dejó ninguna
        /// huérfana (decisión 7 del spec: la falla de concurrencia se detecta antes de publicar).</summary>
        public int Count => _objects.Count;

        public bool IsConfigured => true;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken)
        {
            _objects[publicKey] = 0;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
        {
            _objects.TryRemove(publicKey, out _);
            DeletedKeys.Add(publicKey);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            Task.FromResult(_objects.ContainsKey(publicKey));

        public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
