namespace Modules.Support.Domain;

/// <summary>
/// Los límites de los adjuntos del formulario público (plan F34): hasta <see cref="MaxFiles"/> archivos por ticket, de hasta
/// <see cref="MaxSizeBytes"/> cada uno, con los tipos de Storage y un nombre de archivo simple de hasta <see cref="NameMaxLength"/>.
/// 25 MB, el tope general de Storage, es mucho para un formulario anónimo. Los tipos son una copia de <c>FileUploadPolicy</c>, porque
/// Support no ve a Storage: <c>SupportAttachmentParityTests</c> (Bootstrapper.UnitTests) las ata.
/// </summary>
public static class SupportPublicAttachmentRules
{
    public const int MaxFiles = 5;

    public const long MaxSizeBytes = 10 * 1024 * 1024;

    /// <summary>El de <c>FileUploadPolicy</c> y el de la columna <c>file_name</c> de <c>support.ticket_attachments</c>.</summary>
    public const int NameMaxLength = 260;

    public const int MimeTypeMaxLength = 100;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedMimeTypesByExtension { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>Un nombre de archivo sin ruta, recortado, de 1 a <see cref="NameMaxLength"/>. Mismo criterio que Storage: el nombre
    /// tiene que ser su propio <c>Path.GetFileName</c>.</summary>
    public static bool IsValidName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && trimmed.Length <= NameMaxLength
            && trimmed.IndexOfAny(['/', '\\']) < 0
            && string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal);
    }

    /// <summary>La extensión del nombre está permitida y el tipo MIME es el de esa extensión.</summary>
    public static bool IsAllowedType(string? name, string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        return AllowedMimeTypesByExtension.TryGetValue(Path.GetExtension(name.Trim()), out var allowed)
            && allowed.Contains(mimeType.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsValidSize(long sizeBytes) => sizeBytes is > 0 and <= MaxSizeBytes;
}
