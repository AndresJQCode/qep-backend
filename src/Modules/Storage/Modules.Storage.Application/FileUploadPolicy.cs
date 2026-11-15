using Modules.Storage.Domain;

namespace Modules.Storage.Application;

public static class FileUploadPolicy
{
    public const long MaxSizeBytes = 25 * 1024 * 1024;

    private static readonly Dictionary<string, string[]> AllowedTypes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ["application/pdf"],
            [".doc"] = ["application/msword"],
            [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            [".xls"] = ["application/vnd.ms-excel"],
            [".xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
            [".jpg"] = ["image/jpeg"],
            [".jpeg"] = ["image/jpeg"],
            [".webp"] = ["image/webp"],
            [".png"] = ["image/png"],
        };

    /// <summary>Las extensiones permitidas con sus tipos MIME, de sólo lectura. Lo lee la prueba de paridad de los adjuntos de Soporte
    /// (<c>SupportAttachmentParityTests</c>), que no puede ver la tabla de otra forma.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedMimeTypesByExtension { get; } =
        AllowedTypes.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    public static void ValidateDeclaration(string name, string mimeType, long sizeBytes) =>
        ValidateDeclaration(name, mimeType, sizeBytes, MaxSizeBytes);

    /// <summary>La misma regla con un tope más chico que el general: el de quien abre la sesión sin miembro (Soporte, 10 MB). Un tope
    /// por encima de <see cref="MaxSizeBytes"/> es un error de programación.</summary>
    public static void ValidateDeclaration(string name, string mimeType, long sizeBytes, long maxSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSizeBytes, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSizeBytes, MaxSizeBytes);

        var safeName = Path.GetFileName(name?.Trim());
        if (string.IsNullOrWhiteSpace(safeName) || !string.Equals(safeName, name?.Trim(), StringComparison.Ordinal))
        {
            throw new StorageDomainException(
                "storage.file.name_invalid",
                "The file name is invalid.");
        }

        if (safeName.Length > 260)
        {
            throw new StorageDomainException(
                "storage.file.name_too_long",
                "The file name cannot exceed 260 characters.");
        }

        var extension = Path.GetExtension(safeName);
        if (!AllowedTypes.TryGetValue(extension, out var allowedMimeTypes))
        {
            throw new StorageDomainException(
                "storage.file.type_not_allowed",
                "Only PDF, Word, Excel, JPG, JPEG, WEBP and PNG files are allowed.");
        }

        if (!allowedMimeTypes.Contains(mimeType?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new StorageDomainException(
                "storage.file.mime_mismatch",
                "The media type does not match the file extension.");
        }

        if (sizeBytes <= 0 || sizeBytes > maxSizeBytes)
        {
            throw new StorageDomainException(
                "storage.file.size_invalid",
                $"The file must be between 1 byte and {maxSizeBytes} bytes.");
        }
    }
}

public interface IFileContentInspector
{
    bool Matches(string name, string mimeType, byte[] content);
}
