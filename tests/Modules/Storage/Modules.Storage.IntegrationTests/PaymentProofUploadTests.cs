using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Completar la subida de un comprobante de pago v2 (spec 2026-09-16, sección 1): la imagen se
/// procesa y reemplaza al original en staging/, el PDF queda intacto, ninguno se promueve a files/
/// ni lleva miniatura, y un archivo User sigue el camino de siempre (D13).
/// </summary>
public sealed class PaymentProofUploadTests
{
    // D2, D4, D7 y D8. El endpoint acepta el ownerType nuevo.
    [Fact]
    public async Task AnImageProofIsProcessedToWebpAndStaysInStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var uploaded = await UploadAsync(
            client, factory, "PaymentProof", "comprobante.png", "image/png", await PngAsync(2400, 1200));

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FilePayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("PaymentProof", file.OwnerType);
        Assert.Equal("Available", file.Status);
        Assert.Equal("image/webp", file.MimeType);
        Assert.Equal("comprobante.webp", file.Name);
        Assert.Empty(file.Variants);
        Assert.DoesNotContain(factory.ObjectStorage.Keys, key => key.StartsWith("files/", StringComparison.Ordinal));

        var stored = factory.ObjectStorage.Read(uploaded.StagingKey);
        Assert.Equal("RIFF"u8.ToArray(), stored[..4]);
        Assert.Equal("WEBP"u8.ToArray(), stored[8..12]);
        var info = Image.Identify(stored);
        Assert.Equal(2000, info.Width);
        Assert.Equal(1000, info.Height);
        Assert.Equal(stored.LongLength, file.SizeBytes);

        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.Equal(uploaded.StagingKey, row.StorageKey);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(stored)), row.Checksum);
        Assert.Equal(0L, row.VariantCount);
    }

    // D1: el PDF queda tal cual, también en staging/.
    [Fact]
    public async Task APdfProofStaysUntouchedInStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var uploaded = await UploadAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FilePayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("Available", file.Status);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal("comprobante.pdf", file.Name);
        Assert.Empty(file.Variants);
        Assert.DoesNotContain(factory.ObjectStorage.Keys, key => key.StartsWith("files/", StringComparison.Ordinal));
        Assert.Equal(Pdf(), factory.ObjectStorage.Read(uploaded.StagingKey));
        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.Equal(uploaded.StagingKey, row.StorageKey);
    }

    // Sección 1: «cuarentena y storage.file.rejected / image_processing_failed». Ya pasa antes de
    // esta tarea, por la miniatura; protege el camino nuevo.
    [Fact]
    public async Task AnImageProofThatCannotBeProcessedIsQuarantined()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        byte[] corrupt = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "not really a png"u8.ToArray()];
        var uploaded = await UploadAsync(client, factory, "PaymentProof", "comprobante.png", "image/png", corrupt);

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("storage.image.invalid", problem?.Code);
        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.Equal("Quarantined", row.Status);
        Assert.Equal("image/png", row.MimeType);
    }

    // D13: una imagen User se sigue promoviendo con su miniatura. Ya pasa antes de esta tarea.
    [Fact]
    public async Task AUserImageIsStillPromotedWithItsThumbnail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var uploaded = await UploadAsync(client, factory, "User", "producto.png", "image/png", await PngAsync(640, 320));

        using var response = await CompleteAsync(client, uploaded.FileId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FilePayload>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal("producto.png", file.Name);
        Assert.Equal("thumbnail", Assert.Single(file.Variants).Name);
        var row = await ReadFileAsync(database.GetConnectionString(), uploaded.FileId);
        Assert.StartsWith("files/tenants/", row.StorageKey, StringComparison.Ordinal);
        Assert.False(factory.ObjectStorage.Exists(uploaded.StagingKey));
    }
}
