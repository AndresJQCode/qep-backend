using System.Globalization;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

// Opaque, tenant-scoped physical object key (implementation-baseline Storage):
// The key is not a path the client controls. Browser uploads only receive a staging key;
// validated objects are promoted to a separate immutable final key.
internal static class StorageKey
{
    public static string StagingFor(Guid tenantId, FileResourceId resourceId) =>
        $"staging/tenants/{tenantId:N}/{resourceId.Value:N}";

    public static string FinalFor(
        Guid tenantId,
        FileResourceId resourceId,
        DateTimeOffset createdAt) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"files/tenants/{tenantId:N}/{createdAt:yyyy}/{createdAt:MM}/{resourceId.Value:N}");

    public static string VariantFor(string finalKey, string name, string extension) =>
        $"{finalKey}/variants/{name}.{extension.TrimStart('.').ToLowerInvariant()}";

    public static string PublicFor(Guid tenantId, FileResourceId resourceId, string fileName) =>
        $"tenants/{tenantId:N}/media/{resourceId.Value:N}/original{Path.GetExtension(fileName).ToLowerInvariant()}";

    public static string PublicVariantFor(string publicOriginalKey, FileVariant variant) =>
        $"{publicOriginalKey[..publicOriginalKey.LastIndexOf('/')]}/variants/{variant.Name}{Path.GetExtension(variant.StorageKey).ToLowerInvariant()}";
}
