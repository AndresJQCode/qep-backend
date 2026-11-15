using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Fix round 1 del Bloque D (Soporte fase 3): los archivos de dueño de sistema (<see cref="SystemOwnedFiles"/>, hoy los adjuntos
/// <c>SupportTicket</c> del formulario público) no existen para la consola de Storage. Un miembro con todos los permisos
/// <c>storage.file.*</c> no los lista, no los baja, no los publica, no los borra ni los edita, y no puede abrir una subida con ese
/// dueño. Sólo llegan por los caminos de Soporte. Un archivo de un dueño común sigue funcionando igual. Un host por prueba: el bucket
/// es un doble.
/// </summary>
public sealed class SystemOwnedFilesApiTests
{
    private const string AllPermissions =
        "storage.file.upload,storage.file.read,storage.file.delete,storage.file.publish";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheConsoleCannotOpenAnUploadForASupportTicket()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);

        using var response = await client.PostAsJsonAsync(
            FilesUrl,
            new { ownerId = Guid.NewGuid(), ownerType = "SupportTicket", name = "acta.pdf", mimeType = "application/pdf", sizeBytes = 10 },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("storage.file.owner_type_invalid", (await response.Content.ReadFromJsonAsync<ProblemPayload>(Ct))!.Code);
    }

    [Fact]
    public async Task TheListLeavesSupportAttachmentsOutAndKeepsTheRest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);
        var attachment = await SupportAttachmentAsync(factory, database.GetConnectionString());
        var normal = await CreateAvailableAsync(client, factory, "User", "contrato.pdf", "application/pdf", Pdf());

        var all = await IdsAsync(client, FilesUrl);
        var filtered = await IdsAsync(client, $"{FilesUrl}?ownerId={attachment.OwnerId}&ownerType=SupportTicket");

        Assert.Contains(normal.FileId, all);
        Assert.DoesNotContain(attachment.FileId, all);
        Assert.Empty(filtered);
        Assert.DoesNotContain(attachment.OwnerId.ToString(), await (await client.GetAsync(FilesUrl, Ct)).Content.ReadAsStringAsync(Ct),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryRouteOfASupportAttachmentIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);
        var attachment = await SupportAttachmentAsync(factory, database.GetConnectionString());
        var url = $"{FilesUrl}/{attachment.FileId}";

        var responses = new[]
        {
            await client.PostAsync($"{url}/download-url", content: null, Ct),
            await client.PutAsync($"{url}/publication", content: null, Ct),
            await client.DeleteAsync($"{url}/publication", Ct),
            await client.PatchAsJsonAsync($"{url}/metadata", new { category = "actas", tags = Array.Empty<string>() }, Ct),
            await client.DeleteAsync(url, Ct),
            await client.PostAsync($"{url}/complete", content: null, Ct),
            await client.DeleteAsync($"{url}/upload", Ct),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("storage.file.not_found", (await response.Content.ReadFromJsonAsync<ProblemPayload>(Ct))!.Code);
        }

        var row = await ReadFileAsync(database.GetConnectionString(), attachment.FileId);
        Assert.Equal("Available", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        Assert.True(factory.ObjectStorage.Exists(row.StorageKey));
    }

    [Fact]
    public async Task ANormalFileStillWorksThroughTheConsole()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);
        var normal = await CreateAvailableAsync(client, factory, "User", "contrato.pdf", "application/pdf", Pdf());

        using var download = await client.PostAsync($"{FilesUrl}/{normal.FileId}/download-url", content: null, Ct);
        using var delete = await client.DeleteAsync($"{FilesUrl}/{normal.FileId}", Ct);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    private static HttpClient Member(StorageApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId.ToString());
        client.DefaultRequestHeaders.Add("X-Permissions", AllPermissions);
        return client;
    }

    /// <summary>Un adjunto disponible, subido por el camino de Soporte (sin miembro) con una imagen: también se podría publicar.</summary>
    private static async Task<(Guid FileId, Guid OwnerId)> SupportAttachmentAsync(StorageApiFactory factory, string connectionString)
    {
        var ownerId = Guid.NewGuid();
        var content = await PngAsync(32, 32);
        await using var scope = factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>();
        var session = await uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, ownerId, "foto.png", "image/png", content.Length, 10 * 1024 * 1024, Ct);
        factory.ObjectStorage.Upload((await ReadFileAsync(connectionString, session.FileId)).StorageKey, content);
        Assert.Equal(
            SystemUploadOutcome.Available,
            (await uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct)).Outcome);
        return (session.FileId, ownerId);
    }

    private static async Task<IReadOnlyList<Guid>> IdsAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
    }
}
