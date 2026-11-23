using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Modules.Storage.Infrastructure;

namespace Bootstrapper.UnitTests;

/// <summary>
/// La copia pública de una factura para Zenvia (spec 2026-11-21): la clave es aleatoria y no lleva
/// nada del tenant, el pedido ni la factura, porque la URL queda abierta a quien la tenga.
/// </summary>
public sealed partial class QuotationPdfStorageTests
{
    private static readonly byte[] Pdf = [1, 2, 3];

    [GeneratedRegex("^invoices/[0-9a-f]{32}\\.pdf$")]
    private static partial Regex InvoiceKey();

    [Fact]
    public async Task PublishInvoiceUploadsUnderTheInvoicesPrefixWithARandomKey()
    {
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(publicStorage);

        var first = await storage.PublishInvoiceAsync(Pdf, TestContext.Current.CancellationToken);
        var second = await storage.PublishInvoiceAsync(Pdf, TestContext.Current.CancellationToken);

        Assert.Equal(2, publicStorage.Uploads.Count);
        Assert.All(publicStorage.Uploads, upload =>
        {
            Assert.Matches(InvoiceKey(), upload.Key);
            Assert.Equal("application/pdf", upload.ContentType);
            Assert.Equal(Pdf, upload.Content);
        });
        Assert.NotEqual(publicStorage.Uploads[0].Key, publicStorage.Uploads[1].Key);
        Assert.Equal($"{RecordingPublicObjectStorage.BaseUrl}/{publicStorage.Uploads[0].Key}", first);
        Assert.Equal($"{RecordingPublicObjectStorage.BaseUrl}/{publicStorage.Uploads[1].Key}", second);
    }

    [Fact]
    public async Task ThePublicKeyHasNoTenantOrderOrInvoiceData()
    {
        // Un Guid que el llamador conoce por todos los caminos (tenant, pedido, número de factura).
        // La firma sólo recibe el PDF, pero la prueba fija que nada de eso se cuela en clave ni URL.
        var known = Guid.CreateVersion7();
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(publicStorage);

        var url = await storage.PublishInvoiceAsync(known.ToByteArray(), TestContext.Current.CancellationToken);

        var key = Assert.Single(publicStorage.Uploads).Key;
        Assert.DoesNotContain(known.ToString("N"), key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(known.ToString("N"), url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(known.ToString("D"), url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenants", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishInvoiceFailsWhenThePublicBucketIsNotConfigured()
    {
        var publicStorage = new RecordingPublicObjectStorage { IsConfiguredValue = false };
        var storage = NewStorage(publicStorage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.PublishInvoiceAsync(Pdf, TestContext.Current.CancellationToken));

        Assert.Empty(publicStorage.Uploads);
    }

    // BytesObjectStorage lanza NotSupportedException en UploadAsync: si la copia pública tocara el
    // bucket privado, la prueba fallaría con esa excepción.
    [Fact]
    public async Task PublishInvoiceDoesNotTouchThePrivateBucket()
    {
        var publicStorage = new RecordingPublicObjectStorage();
        var storage = NewStorage(publicStorage);

        await storage.PublishInvoiceAsync(Pdf, TestContext.Current.CancellationToken);

        Assert.Empty(publicStorage.Copies);
        Assert.Single(publicStorage.Uploads);
    }

    private static QuotationPdfStorage NewStorage(RecordingPublicObjectStorage publicStorage) =>
        new(new BytesObjectStorage(), publicStorage, Options.Create(new StorageOptions()));
}
