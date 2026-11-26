using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Fix round 1 del Bloque D (Soporte fase 3): los archivos de dueño de sistema (<see cref="SystemOwnedFiles"/>: los adjuntos
/// <c>SupportTicket</c> del formulario público y, desde el spec 2026-11-17, las imágenes de encabezado <c>MessageTemplate</c>) no existen
/// para la consola de Storage. Un miembro con todos los permisos
/// <c>storage.file.*</c> no los lista, no los baja, no los publica, no los borra ni los edita, y no puede abrir una subida con ese
/// dueño. Sólo llegan por los caminos de su módulo. Cada prueba corre con cada dueño de sistema (<see cref="SystemOwners"/>, que una
/// prueba ata a <see cref="SystemOwnedFiles.OwnerTypes"/>). Un archivo de un dueño común sigue funcionando igual. Un host por prueba: el bucket
/// es un doble.
/// </summary>
public sealed class SystemOwnedFilesApiTests
{
    private const string AllPermissions =
        "storage.file.upload,storage.file.read,storage.file.delete,storage.file.publish";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<FileOwnerType> SystemOwners => new(FileOwnerType.SupportTicket, FileOwnerType.MessageTemplate, FileOwnerType.ChatbotMedia, FileOwnerType.HrDocument);

    // Un dueño de sistema nuevo tiene que entrar a SystemOwners: si no, estas pruebas dejan de cubrirlo sin ponerse rojas.
    [Fact]
    public void SystemOwnersCoversEverySystemOwnedFile()
    {
        Assert.Equal(SystemOwnedFiles.OwnerTypes.Order(), SystemOwners.Select(row => row.Data).Order());
    }

    [Theory]
    [MemberData(nameof(SystemOwners))]
    public async Task TheConsoleCannotOpenAnUploadForASystemOwner(FileOwnerType ownerType)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);

        using var response = await client.PostAsJsonAsync(
            FilesUrl,
            new { ownerId = Guid.NewGuid(), ownerType = ownerType.ToString(), name = "acta.pdf", mimeType = "application/pdf", sizeBytes = 10 },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("storage.file.owner_type_invalid", (await response.Content.ReadFromJsonAsync<ProblemPayload>(Ct))!.Code);
    }

    [Theory]
    [MemberData(nameof(SystemOwners))]
    public async Task TheListLeavesSystemOwnedFilesOutAndKeepsTheRest(FileOwnerType ownerType)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);
        var attachment = await SystemOwnedFileAsync(factory, database.GetConnectionString(), ownerType);
        var normal = await CreateAvailableAsync(client, factory, "User", "contrato.pdf", "application/pdf", Pdf());

        var all = await IdsAsync(client, FilesUrl);
        var filtered = await IdsAsync(client, $"{FilesUrl}?ownerId={attachment.OwnerId}&ownerType={ownerType}");

        Assert.Contains(normal.FileId, all);
        Assert.DoesNotContain(attachment.FileId, all);
        Assert.Empty(filtered);
        // El dueño de una imagen de plantilla o de un archivo de chatbot es el tenant, que la respuesta no lleva; el de un adjunto es un
        // secreto que no se filtra.
        if (ownerType is not (FileOwnerType.MessageTemplate or FileOwnerType.ChatbotMedia or FileOwnerType.HrDocument))
        {
            Assert.DoesNotContain(attachment.OwnerId.ToString(), await (await client.GetAsync(FilesUrl, Ct)).Content.ReadAsStringAsync(Ct),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [MemberData(nameof(SystemOwners))]
    public async Task EveryRouteOfASystemOwnedFileIsNotFound(FileOwnerType ownerType)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = Member(factory);
        var attachment = await SystemOwnedFileAsync(factory, database.GetConnectionString(), ownerType);
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

    /// <summary>Un archivo disponible de un dueño de sistema, subido por el camino sin miembro con una imagen: también se podría publicar.
    /// El dueño de un adjunto de Soporte es un secreto aleatorio; el de una imagen de plantilla o de un archivo de chatbot, el tenant.</summary>
    private static async Task<(Guid FileId, Guid OwnerId)> SystemOwnedFileAsync(
        StorageApiFactory factory, string connectionString, FileOwnerType ownerType)
    {
        var ownerId = ownerType is FileOwnerType.MessageTemplate or FileOwnerType.ChatbotMedia or FileOwnerType.HrDocument ? TenantId : Guid.NewGuid();
        var content = await PngAsync(32, 32);
        await using var scope = factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>();
        var session = await uploads.OpenAsync(
            TenantId, ownerType, ownerId, "foto.png", "image/png", content.Length, 10 * 1024 * 1024, Ct);
        factory.ObjectStorage.Upload((await ReadFileAsync(connectionString, session.FileId)).StorageKey, content);
        Assert.Equal(
            SystemUploadOutcome.Available,
            (await uploads.CompleteAsync(TenantId, session.FileId, ownerType, ownerId, Ct)).Outcome);
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
