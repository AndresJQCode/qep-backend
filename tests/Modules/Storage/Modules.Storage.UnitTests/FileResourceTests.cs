using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

public sealed class FileResourceTests
{
    private static FileResource NewPending() =>
        FileResource.CreatePendingUpload(
            FileResourceId.New(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FileOwnerType.User,
            "invoice.pdf",
            "application/pdf",
            1024,
            "tenants/a/2026/07/b",
            DateTimeOffset.UtcNow);

    private static FileResource NewPendingImage() =>
        FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.User,
            "product.jpg", "image/jpeg", 1024, "staging/product", DateTimeOffset.UtcNow);

    [Fact]
    public void CreatePendingUploadAcceptsTenantAsOwnerType()
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.Tenant,
            "logo.png", "image/png", 1024, "staging/tenant-logo", DateTimeOffset.UtcNow);

        Assert.Equal(FileOwnerType.Tenant, resource.OwnerType);
    }

    [Fact]
    public void CompleteThenCleanReachesAvailable()
    {
        var resource = NewPending();
        Assert.Equal(FileResourceStatus.PendingUpload, resource.Status);

        resource.CompleteUpload("abc123", 2048, DateTimeOffset.UtcNow);
        Assert.Equal(FileResourceStatus.PendingScan, resource.Status);
        Assert.Equal(2048, resource.SizeBytes);
        Assert.Equal("abc123", resource.Checksum);

        resource.MarkClean(DateTimeOffset.UtcNow);
        Assert.Equal(FileResourceStatus.Available, resource.Status);
    }

    [Fact]
    public void PromoteChangesThePhysicalKeyAndMakesFileAvailable()
    {
        var resource = NewPending();
        resource.CompleteUpload("abc123", 1024, DateTimeOffset.UtcNow);

        resource.Promote("files/tenants/final", DateTimeOffset.UtcNow);

        Assert.Equal(FileResourceStatus.Available, resource.Status);
        Assert.Equal("files/tenants/final", resource.StorageKey);
        resource.EnsureDownloadable();
    }

    [Fact]
    public void QuarantineBlocksDownload()
    {
        var resource = NewPending();
        resource.CompleteUpload("abc", 10, DateTimeOffset.UtcNow);
        resource.Quarantine(DateTimeOffset.UtcNow);

        Assert.Equal(FileResourceStatus.Quarantined, resource.Status);
        Assert.Throws<StorageDomainException>(() => resource.EnsureDownloadable());
    }

    [Fact]
    public void CompleteRejectsEmptyObject()
    {
        var resource = NewPending();
        Assert.Throws<StorageDomainException>(
            () => resource.CompleteUpload("abc", 0, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AbandonedPendingUploadCanBePurged()
    {
        var resource = NewPending();

        resource.PurgeAbandonedUpload(DateTimeOffset.UtcNow);

        Assert.Equal(FileResourceStatus.Purged, resource.Status);
    }

    [Fact]
    public void MetadataNormalizesCategoryAndTags()
    {
        var resource = NewPending();

        resource.UpdateMetadata(
            "  Contratos  ",
            [" Legal ", "legal", "Cliente VIP"],
            DateTimeOffset.UtcNow);

        Assert.Equal("Contratos", resource.Category);
        Assert.Equal(["legal", "cliente vip"], resource.Tags);
    }

    [Fact]
    public void MetadataRejectsTooManyTags()
    {
        var resource = NewPending();

        Assert.Throws<StorageDomainException>(() => resource.UpdateMetadata(
            "Documentos",
            Enumerable.Range(1, 11).Select(value => $"tag-{value}"),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void VariantCanBeAddedWhileImageIsPendingScan()
    {
        var resource = NewPending();
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);

        resource.AddVariant(
            "thumbnail",
            "files/thumbnail.webp",
            "image/webp",
            320,
            160,
            512);

        var thumbnail = resource.GetVariant("THUMBNAIL");
        Assert.Equal(320, thumbnail.Width);
        Assert.Equal("image/webp", thumbnail.MimeType);
    }

    [Fact]
    public void CompleteOnlyAppliesOnce()
    {
        var resource = NewPending();
        resource.CompleteUpload("abc", 10, DateTimeOffset.UtcNow);
        Assert.Throws<StorageDomainException>(
            () => resource.CompleteUpload("abc", 10, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AvailableImageCanBePublishedAndUnpublished()
    {
        var resource = NewPendingImage();
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);
        resource.Promote("files/product", DateTimeOffset.UtcNow);

        resource.Publish("public/product.jpg", DateTimeOffset.UtcNow);

        Assert.True(resource.IsPublic);
        Assert.Equal("public/product.jpg", resource.PublicStorageKey);
        Assert.NotNull(resource.PublishedAt);

        resource.Unpublish(DateTimeOffset.UtcNow);
        Assert.False(resource.IsPublic);
        Assert.Null(resource.PublicStorageKey);
    }

    [Fact]
    public void DocumentsCannotBePublished()
    {
        var resource = NewPending();
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);
        resource.Promote("files/document", DateTimeOffset.UtcNow);

        Assert.Throws<StorageDomainException>(() =>
            resource.Publish("public/document.pdf", DateTimeOffset.UtcNow));
    }

    // Spec 2026-11-18: el multimedia de un chatbot se manda por link, así que un PDF también se publica.
    [Fact]
    public void AChatbotMediaPdfCanBePublished()
    {
        var resource = NewChatbotMedia("catalogo.pdf", "application/pdf");
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);
        resource.Promote("files/catalogo", DateTimeOffset.UtcNow);

        resource.Publish("public/catalogo.pdf", DateTimeOffset.UtcNow);

        Assert.Equal("public/catalogo.pdf", resource.PublicStorageKey);
    }

    [Fact]
    public void AProductPdfStillCannotBePublished()
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.Product,
            "ficha.pdf", "application/pdf", 1024, "staging/ficha", DateTimeOffset.UtcNow);
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);
        resource.Promote("files/ficha", DateTimeOffset.UtcNow);

        var error = Assert.Throws<StorageDomainException>(() =>
            resource.Publish("public/ficha.pdf", DateTimeOffset.UtcNow));

        Assert.Equal("storage.file.public_image_required", error.Code);
    }

    // Una imagen grande de un chatbot se reemplaza por su versión comprimida antes de quedar Available.
    [Fact]
    public void AChatbotMediaImageCanBeReplacedByItsProcessedVersion()
    {
        var resource = NewChatbotMedia("catalogo.png", "image/png");
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);

        resource.ReplaceContentWithProcessedImage("image/jpeg", ".jpg", "shrunk", 512, DateTimeOffset.UtcNow);

        Assert.Equal("catalogo.jpg", resource.Name);
        Assert.Equal("image/jpeg", resource.MimeType);
        Assert.Equal(512, resource.SizeBytes);
    }

    private static FileResource NewChatbotMedia(string name, string mimeType) =>
        FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.ChatbotMedia,
            name, mimeType, 1024, "staging/chatbot-media", DateTimeOffset.UtcNow);

    [Fact]
    public void OnlyAvailableIsDownloadable()
    {
        var resource = NewPending();
        Assert.Throws<StorageDomainException>(() => resource.EnsureDownloadable());

        resource.CompleteUpload("abc", 10, DateTimeOffset.UtcNow);
        resource.MarkClean(DateTimeOffset.UtcNow);
        resource.EnsureDownloadable();

        resource.SoftDelete(DateTimeOffset.UtcNow);
        Assert.Equal(FileResourceStatus.Deleted, resource.Status);
        Assert.Throws<StorageDomainException>(() => resource.EnsureDownloadable());
    }

    private static FileResource NewProductImage(bool available = true)
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.Product,
            "product.jpg", "image/jpeg", 1024, "staging/product", DateTimeOffset.UtcNow);
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);
        if (available)
        {
            resource.MarkClean(DateTimeOffset.UtcNow);
        }

        return resource;
    }

    [Fact]
    public void PurgeReleasedProductImageMarksAnAvailableProductImagePurged()
    {
        var resource = NewProductImage();
        resource.Publish("public/product.jpg", DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow.AddMinutes(5);

        resource.PurgeReleasedProductImage(now);

        Assert.Equal(FileResourceStatus.Purged, resource.Status);
        Assert.Equal(now, resource.DeletedAt);
        Assert.Equal(now, resource.UpdatedAt);
        Assert.Equal("public/product.jpg", resource.PublicStorageKey);
    }

    [Fact]
    public void PurgeReleasedProductImageRequiresAvailable()
    {
        var resource = NewProductImage(available: false);
        Assert.Equal(FileResourceStatus.PendingScan, resource.Status);

        var ex = Assert.Throws<StorageDomainException>(
            () => resource.PurgeReleasedProductImage(DateTimeOffset.UtcNow));

        Assert.Equal("storage.file.invalid_state", ex.Code);
    }

    [Fact]
    public void PurgeReleasedProductImageRequiresAProductImage()
    {
        var resource = NewPending();
        resource.CompleteUpload("abc", 10, DateTimeOffset.UtcNow);
        resource.MarkClean(DateTimeOffset.UtcNow);

        var ex = Assert.Throws<StorageDomainException>(
            () => resource.PurgeReleasedProductImage(DateTimeOffset.UtcNow));

        Assert.Equal("storage.file.invalid_state", ex.Code);
        Assert.Equal(FileResourceStatus.Available, resource.Status);
    }

    private static FileResource NewHrDocument(bool available = true)
    {
        var resource = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.NewGuid(), Guid.NewGuid(), FileOwnerType.HrDocument,
            "contrato.pdf", "application/pdf", 1024, "staging/contrato", DateTimeOffset.UtcNow);
        resource.CompleteUpload("abc", 1024, DateTimeOffset.UtcNow);
        if (available)
        {
            resource.MarkClean(DateTimeOffset.UtcNow);
        }

        return resource;
    }

    [Fact]
    public void PurgeReleasedHrDocumentMarksAnAvailableHrDocumentPurged()
    {
        var resource = NewHrDocument();
        var now = DateTimeOffset.UtcNow.AddMinutes(5);

        resource.PurgeReleasedHrDocument(now);

        Assert.Equal(FileResourceStatus.Purged, resource.Status);
        Assert.Equal(now, resource.DeletedAt);
        Assert.Equal(now, resource.UpdatedAt);
    }

    [Fact]
    public void PurgeReleasedHrDocumentRequiresAvailable()
    {
        var resource = NewHrDocument(available: false);
        Assert.Equal(FileResourceStatus.PendingScan, resource.Status);

        var ex = Assert.Throws<StorageDomainException>(
            () => resource.PurgeReleasedHrDocument(DateTimeOffset.UtcNow));

        Assert.Equal("storage.file.invalid_state", ex.Code);
        Assert.Equal(FileResourceStatus.PendingScan, resource.Status);
    }

    [Fact]
    public void PurgeReleasedHrDocumentRequiresAnHrDocument()
    {
        var resource = NewProductImage();

        var ex = Assert.Throws<StorageDomainException>(
            () => resource.PurgeReleasedHrDocument(DateTimeOffset.UtcNow));

        Assert.Equal("storage.file.invalid_state", ex.Code);
        Assert.Equal(FileResourceStatus.Available, resource.Status);
        Assert.Null(resource.DeletedAt);
    }
}
