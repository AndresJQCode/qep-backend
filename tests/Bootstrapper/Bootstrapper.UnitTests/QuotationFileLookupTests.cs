using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cómo ve Quotations la disponibilidad de un archivo de Storage para adjuntarlo a un pedido. Desde la
/// enmienda D16 del spec 2026-09-16, un comprobante ya movido al bucket público no está disponible:
/// su temporal ya no existe y la copia al público no tendría de dónde salir.
/// </summary>
public sealed class QuotationFileLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APaymentProofNotYetMovedIsAvailable()
    {
        var proof = AvailablePaymentProof();

        var found = await LookupWith(proof).FindAsync(
            TenantId, proof.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.True(found.IsAvailable);
        // Revisión final (I2): Quotations necesita saberlo para rechazar un comprobante ya adjunto.
        Assert.True(found.IsPaymentProof);
    }

    [Fact]
    public async Task AMovedPaymentProofIsNotAvailable()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic("payment-proofs/abc.webp", Now);

        var found = await LookupWith(proof).FindAsync(
            TenantId, proof.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.False(found.IsAvailable);
        Assert.Equal("image/webp", found.MimeType);
    }

    // Sólo un PaymentProof: una imagen User publicada también tiene PublicStorageKey, y su original
    // sigue en el bucket privado (D13).
    [Fact]
    public async Task APublishedUserImageIsStillAvailable()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User, "captura.png",
            "image/png", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        image.CompleteUpload("checksum", 1024, Now);
        image.Promote($"files/tenants/{TenantId:N}/{Guid.CreateVersion7():N}", Now);
        image.Publish($"tenants/{TenantId:N}/media/captura/original.png", Now);

        var found = await LookupWith(image).FindAsync(
            TenantId, image.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.True(found.IsAvailable);
        Assert.False(found.IsPaymentProof);
    }

    private static FileResource AvailablePaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof, "comprobante.webp",
            "image/webp", 1024, $"staging/tenants/{TenantId:N}/{Guid.CreateVersion7():N}", Now);
        proof.CompleteUpload("checksum", 1024, Now);
        proof.MarkClean(Now);
        return proof;
    }

    private static QuotationFileLookup LookupWith(FileResource resource) =>
        new(
            new InMemoryFileResourceRepository(resource),
            new UnusedObjectStorage(),
            Options.Create(new QuotationsOptions()));

    // FindAsync no firma ni lee bytes: cualquier llamada al bucket privado es un error de la prueba.
    private sealed class UnusedObjectStorage : IObjectStorage
    {
        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PromoteAsync(
            string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
