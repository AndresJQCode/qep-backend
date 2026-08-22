using System.Globalization;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

// Clave física de objeto, opaca y acotada al tenant (Storage, línea base de implementación):
// La clave no es un path que el cliente controle. Las subidas del navegador sólo reciben una
// clave de staging; los objetos validados se promueven a una clave final inmutable aparte.
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
