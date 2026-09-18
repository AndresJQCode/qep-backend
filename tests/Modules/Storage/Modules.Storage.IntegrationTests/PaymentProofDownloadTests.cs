using System.Net;
using System.Net.Http.Json;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// La descarga de un comprobante desde la app (spec 2026-09-16, D5 y sección 3): antes de moverse se
/// firma el temporal como cualquier archivo; ya movido, la URL es la pública, porque el temporal ya
/// no existe.
/// </summary>
public sealed class PaymentProofDownloadTests
{
    [Fact]
    public async Task TheDownloadUrlOfAMovedProofIsItsPublicUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));

        using var response = await client.PostAsync(
            $"{FilesUrl}/{proof.FileId}/download-url", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var download = await response.Content.ReadFromJsonAsync<DownloadUrlPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal($"{InMemoryPublicObjectStorage.BaseUrl}/{publicKey}", download?.Url);
    }

    // Ya pasa antes de esta tarea: protege el camino de siempre mientras el comprobante espera.
    [Fact]
    public async Task TheDownloadUrlOfAProofNotYetMovedIsSignedFromStaging()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());

        using var response = await client.PostAsync(
            $"{FilesUrl}/{proof.FileId}/download-url", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var download = await response.Content.ReadFromJsonAsync<DownloadUrlPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal($"https://r2.test/{proof.StagingKey}", download?.Url);
    }

    private sealed record DownloadUrlPayload(string Url);
}
