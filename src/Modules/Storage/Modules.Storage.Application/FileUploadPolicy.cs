using Modules.Storage.Domain;

namespace Modules.Storage.Application;

public static class FileUploadPolicy
{
    public const long MaxSizeBytes = 25 * 1024 * 1024;

    /// <summary>Spec 2026-11-18: el tope del multimedia de un chatbot es el del documento de WhatsApp (100 MB). Cada tipo tiene su propio
    /// tope, más chico, que valida Automations con la política de Mensajería antes de abrir la sesión.</summary>
    public const long ChatbotMediaMaxSizeBytes = 100L * 1024 * 1024;

    /// <summary>Spec 2026-11-18: el tope de Meta para una imagen enviada por link. Una imagen de chatbot más grande se comprime al
    /// completar.</summary>
    public const long ChatbotMediaLinkImageMaxBytes = 5L * 1024 * 1024;

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

    // Spec 2026-11-18: lo que un chatbot manda además de la tabla general (la columna WhatsApp de OutboundMediaPolicy de Mensajería,
    // que una prueba de paridad del Bootstrapper compara). Sólo para ChatbotMedia: la consola no acepta video, audio, PowerPoint ni texto.
    private static readonly Dictionary<string, string[]> ChatbotMediaTypes =
        new Dictionary<string, string[]>(AllowedTypes, StringComparer.OrdinalIgnoreCase)
        {
            [".ppt"] = ["application/vnd.ms-powerpoint"],
            [".pptx"] = ["application/vnd.openxmlformats-officedocument.presentationml.presentation"],
            [".txt"] = ["text/plain"],
            [".mp4"] = ["video/mp4"],
            [".3gp"] = ["video/3gpp"],
            [".mp3"] = ["audio/mpeg"],
            [".m4a"] = ["audio/mp4"],
            [".aac"] = ["audio/aac"],
            [".amr"] = ["audio/amr"],
            [".ogg"] = ["audio/ogg"],
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
    public static void ValidateDeclaration(string name, string mimeType, long sizeBytes, long maxSizeBytes) =>
        Validate(name, mimeType, sizeBytes, maxSizeBytes, MaxSizeBytes, AllowedTypes);

    /// <summary>El tope máximo que admite un tipo de dueño: <see cref="ChatbotMediaMaxSizeBytes"/> para el multimedia de un chatbot,
    /// <see cref="MaxSizeBytes"/> para el resto.</summary>
    public static long MaxSizeBytesFor(FileOwnerType ownerType) =>
        ownerType is FileOwnerType.ChatbotMedia ? ChatbotMediaMaxSizeBytes : MaxSizeBytes;

    /// <summary>La regla según el dueño (spec 2026-11-18): <see cref="FileOwnerType.ChatbotMedia"/> suma video y audio a la tabla
    /// general y sube el techo a <see cref="ChatbotMediaMaxSizeBytes"/>; cualquier otro dueño valida igual que la sobrecarga de cuatro
    /// argumentos. Un tope por encima del techo del dueño es un error de programación.</summary>
    public static void ValidateDeclaration(
        string name, string mimeType, long sizeBytes, long maxSizeBytes, FileOwnerType ownerType) =>
        Validate(
            name,
            mimeType,
            sizeBytes,
            maxSizeBytes,
            MaxSizeBytesFor(ownerType),
            ownerType is FileOwnerType.ChatbotMedia ? ChatbotMediaTypes : AllowedTypes);

    private static void Validate(
        string name,
        string mimeType,
        long sizeBytes,
        long maxSizeBytes,
        long ceilingBytes,
        Dictionary<string, string[]> allowedTypes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSizeBytes, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSizeBytes, ceilingBytes);

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
        if (!allowedTypes.TryGetValue(extension, out var allowedMimeTypes))
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
