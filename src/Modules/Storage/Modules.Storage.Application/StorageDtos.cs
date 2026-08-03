using Modules.Storage.Domain;

namespace Modules.Storage.Application;

public sealed record UploadSessionDto(Guid FileResourceId, string UploadUrl, string StorageKey);

public sealed record FileResourceDto(
    Guid Id,
    Guid TenantId,
    Guid OwnerId,
    string OwnerType,
    string Name,
    string MimeType,
    long SizeBytes,
    string Status,
    string? Category,
    IReadOnlyList<string> Tags,
    bool IsPublic,
    string? PublicUrl,
    IReadOnlyList<FileVariantDto> Variants,
    DateTimeOffset CreatedAt);

public sealed record FileVariantDto(
    string Name,
    string MimeType,
    int Width,
    int Height,
    long SizeBytes,
    string? PublicUrl);

public sealed record DownloadUrlDto(string Url);

public sealed record SoftDeleteResult(bool Deleted);

public sealed record PagedFilesDto(
    IReadOnlyList<FileResourceDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

internal static class FileResourceMapping
{
    public static FileResourceDto ToDto(this FileResource resource, IPublicObjectStorage? publicStorage = null) =>
        new(
            resource.Id.Value,
            resource.TenantId,
            resource.OwnerId,
            resource.OwnerType.ToString(),
            resource.Name,
            resource.MimeType,
            resource.SizeBytes,
            resource.Status.ToString(),
            resource.Category,
            resource.Tags,
            resource.IsPublic,
            PublicUrl(resource, publicStorage),
            resource.Variants
                .Select(variant => new FileVariantDto(
                    variant.Name,
                    variant.MimeType,
                    variant.Width,
                    variant.Height,
                    variant.SizeBytes,
                    VariantPublicUrl(resource, variant, publicStorage)))
                .ToArray(),
            resource.CreatedAt);

    private static string? PublicUrl(FileResource resource, IPublicObjectStorage? storage) =>
        resource.PublicStorageKey is { } key && storage?.IsConfigured == true
            ? storage.GetUrl(key)
            : null;

    private static string? VariantPublicUrl(
        FileResource resource, FileVariant variant, IPublicObjectStorage? storage) =>
        resource.PublicStorageKey is { } key && storage?.IsConfigured == true
            ? storage.GetUrl(StorageKey.PublicVariantFor(key, variant))
            : null;
}
