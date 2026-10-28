using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

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

    [Fact]
    public async Task AProductImageWithCatalogOffIsForbidden()
    {
        var image = AvailableFile(FileOwnerType.Product);

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            HandlerFor(image, new RecordingStorageAuditPublisher(), FixedTenantModules.AllBut(TenantModuleKeys.Catalog))
                .HandleAsync(new IssueDownloadUrlCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
    }

    // Regresión de B10/B11: un comprobante ya movido al bucket público (PublicStorageKey) con orders
    // apagado no devuelve su URL pública. El guard va antes de armarla, así que no se construye URL
    // ni se audita ni se guarda nada.
    [Fact]
    public async Task AMovedPaymentProofWithOrdersOffIsForbiddenAndNoUrlIsBuilt()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);
        var audit = new RecordingStorageAuditPublisher();
        var publicStorage = new UrlCountingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();
        var handler = new IssueDownloadUrlHandler(
            new InMemoryFileResourceRepository(proof),
            FixedTenantModules.AllBut(TenantModuleKeys.Orders),
            new SigningObjectStorage(),
            publicStorage,
            unitOfWork,
            audit,
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(
                new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Equal(0, publicStorage.GetUrlCalls);
        Assert.Empty(audit.Actions);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // El logo es núcleo: se descarga aunque el tenant no tenga ningún módulo.
    [Fact]
    public async Task ATenantLogoIsSignedWithNoModules()
    {
        var logo = AvailableFile(FileOwnerType.Tenant);

        var result = await HandlerFor(logo, new RecordingStorageAuditPublisher(), new FixedTenantModules(TenantModuleSet.Empty))
            .HandleAsync(new IssueDownloadUrlCommand(TenantId, logo.Id.Value), TestContext.Current.CancellationToken);

        Assert.StartsWith(SigningObjectStorage.SignedBaseUrl, result.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASimulatedTenantStillGetsTheUrlOfAProof()
    {
        var proof = AvailablePaymentProof();

        var result = await HandlerFor(proof, new RecordingStorageAuditPublisher(), FixedTenantModules.Simulated)
            .HandleAsync(new IssueDownloadUrlCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.StartsWith(SigningObjectStorage.SignedBaseUrl, result.Url, StringComparison.Ordinal);
    }

    private static FileResource AvailableFile(FileOwnerType ownerType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), ownerType,
            "archivo.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/archivo", Now);
        file.CompleteUpload("checksum", 2048, Now);
        file.MarkClean(Now);
        return file;
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

    /// <summary>El bucket público que cuenta cuántas URL se armaron.</summary>
    private sealed class UrlCountingPublicObjectStorage : IPublicObjectStorage
    {
        public int GetUrlCalls { get; private set; }

        public bool IsConfigured => true;

        public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string GetUrl(string publicKey)
        {
            GetUrlCalls++;
            return $"{FixedPublicObjectStorage.BaseUrl}/{publicKey}";
        }

        public Task<PublicObjectPage> ListAsync(
            string prefix, string? continuationToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static IssueDownloadUrlHandler HandlerFor(
        FileResource resource, RecordingStorageAuditPublisher audit, ITenantModules? modules = null) =>
        new(
            new InMemoryFileResourceRepository(resource),
            modules ?? FixedTenantModules.Simulated,
            new SigningObjectStorage(),
            new FixedPublicObjectStorage(),
            new CountingStorageUnitOfWork(),
            audit,
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
}
