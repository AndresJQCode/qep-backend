using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// D5 (spec 2026-09-16): la URL pública sólo para un PaymentProof ya movido; el resto se firma como
/// siempre, incluida una imagen User publicada, y la auditoría se registra en los dos casos.
/// </summary>
public sealed class IssueDownloadUrlHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMovedPaymentProofGetsItsPublicUrl()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);
        var audit = new RecordingStorageAuditPublisher();

        var result = await HandlerFor(proof, audit).HandleAsync(
            new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal($"{FixedPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf", result.Url);
        Assert.Equal(["storage.file.downloaded"], audit.Actions);
    }

    [Fact]
    public async Task APaymentProofNotYetMovedIsSignedFromStaging()
    {
        var proof = AvailablePaymentProof();
        var audit = new RecordingStorageAuditPublisher();

        var result = await HandlerFor(proof, audit).HandleAsync(
            new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal($"{SigningObjectStorage.SignedBaseUrl}/{proof.StorageKey}?signature=test", result.Url);
        Assert.Equal(["storage.file.downloaded"], audit.Actions);
    }

    // Sólo un PaymentProof: una imagen de producto publicada también tiene PublicStorageKey, y su
    // descarga desde la app sigue siendo el original firmado.
    [Fact]
    public async Task APublishedUserImageIsStillSigned()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/producto", Now);
        image.CompleteUpload("checksum", 2048, Now);
        image.Promote($"files/tenants/{TenantId:N}/producto", Now);
        image.Publish($"tenants/{TenantId:N}/media/producto/original.png", Now);

        var result = await HandlerFor(image, new RecordingStorageAuditPublisher()).HandleAsync(
            new IssueDownloadUrlCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal(
            $"{SigningObjectStorage.SignedBaseUrl}/files/tenants/{TenantId:N}/producto?signature=test",
            result.Url);
    }

    private static FileResource AvailablePaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        proof.CompleteUpload("checksum", 2048, Now);
        proof.MarkClean(Now);
        return proof;
    }

    private static IssueDownloadUrlHandler HandlerFor(
        FileResource resource, RecordingStorageAuditPublisher audit) =>
        new(
            new InMemoryFileResourceRepository(resource),
            new SigningObjectStorage(),
            new FixedPublicObjectStorage(),
            new CountingStorageUnitOfWork(),
            audit,
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
}
