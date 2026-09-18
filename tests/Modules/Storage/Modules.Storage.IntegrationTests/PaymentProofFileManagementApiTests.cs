using System.Net;
using System.Net.Http.Json;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Borrar un comprobante movido desde la API de Storage (spec 2026-09-16, D15): si un pedido lo
/// referencia, su copia pública es la evidencia del pago que enlaza el Excel y no se toca; si nadie
/// lo referencia, se borra como cualquier archivo publicado.
/// </summary>
public sealed class PaymentProofFileManagementApiTests
{
    [Fact]
    public async Task DeletingAMovedProofThatAnOrderReferencesIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        factory.FileReferences.Reference(fileId);

        using var response = await client.DeleteAsync(
            $"{FilesUrl}/{fileId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal("storage.file.invalid_state", problem?.Code);
        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Available", row.Status);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.True(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
    }

    // Lo de siempre: sin referencia, borrar un archivo publicado borra su copia. Ya pasa antes de esta
    // tarea.
    [Fact]
    public async Task DeletingAMovedProofThatNoOrderReferencesWorksAsBefore()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);

        using var response = await client.DeleteAsync(
            $"{FilesUrl}/{fileId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Deleted", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.False(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Equal([publicKey], factory.PublicObjectStorage.DeletedKeys);
    }

    // Un comprobante v2 ya movido, con su copia en el bucket público en memoria.
    private static async Task<(Guid FileId, string PublicKey)> MovedProofAsync(
        HttpClient client, StorageApiFactory factory)
    {
        var proof = await CreateAvailableAsync(
            client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(publicKey, DateTimeOffset.UtcNow);
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        return (proof.FileId, publicKey);
    }
}
