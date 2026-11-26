namespace Modules.Support.Domain;

/// <summary>
/// Los límites de los adjuntos del formulario público (plan F34; owner, 2026-11-15): hasta <see cref="MaxFiles"/> archivos por ticket,
/// de hasta <see cref="MaxSizeBytes"/> cada uno, sólo PDF, JPEG, PNG y WebP, y un nombre de archivo simple de hasta
/// <see cref="NameMaxLength"/>. 25 MB, el tope general de Storage, es mucho para un formulario anónimo. Word y Excel quedan fuera mientras
/// ClamAV esté apagado en producción: un adjunto anónimo con macros lo abre un asesor. La lista es un subconjunto de la de
/// <c>FileUploadPolicy</c>, porque Support no ve a Storage: <c>SupportAttachmentParityTests</c> (Bootstrapper.UnitTests) exige que Storage
/// acepte cada tipo.
/// </summary>
public static class SupportPublicAttachmentRules
{
    public const int MaxFiles = 3;

    public const long MaxSizeBytes = 10 * 1024 * 1024;

    /// <summary>El de <c>FileUploadPolicy</c> y el de la columna <c>file_name</c> de <c>support.ticket_attachments</c>.</summary>
    public const int NameMaxLength = 260;

    public const int MimeTypeMaxLength = 100;

    /// <summary>
    /// Owner, 2026-11-15 (E2): a lo sumo tantas subidas abiertas por tenant en <see cref="PendingWindow"/> que ningún ticket reclamó.
    /// El limitador por IP no frena a muchas IPs a la vez llenando el bucket; este tope sí. Las reclamadas no cuentan, y las demás las
    /// purga el barrido de staging de Storage pasadas 24 h. Pasado el tope, abrir responde 422 <c>support.public.attachments_busy</c>.
    /// </summary>
    public const int MaxPendingPerTenantPerDay = 200;

    /// <summary>La ventana de <see cref="MaxPendingPerTenantPerDay"/>: la misma retención del staging de Storage.</summary>
    public static TimeSpan PendingWindow { get; } = TimeSpan.FromHours(24);

    /// <summary><c>image/jpg</c> no es un tipo registrado, pero algunos navegadores y el <c>accept</c> de la página lo usan: se toma como
    /// <c>image/jpeg</c> (<see cref="NormalizeMimeType"/>), que es lo que Storage acepta y lo que se firma en el PUT.</summary>
    public const string JpgAlias = "image/jpg";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedMimeTypesByExtension { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ["application/pdf"],
            [".jpg"] = ["image/jpeg"],
            [".jpeg"] = ["image/jpeg"],
            [".webp"] = ["image/webp"],
            [".png"] = ["image/png"],
        };

    /// <summary>Los tipos MIME que el formulario acepta, sin repetir: la página arma su <c>accept</c> con ellos.</summary>
    public static IReadOnlyList<string> MimeTypes { get; } =
        AllowedMimeTypesByExtension.Values.SelectMany(types => types).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>El tipo recortado y en minúsculas, con <see cref="JpgAlias"/> llevado a <c>image/jpeg</c>.</summary>
    public static string NormalizeMimeType(string mimeType)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        var normalized = mimeType.Trim().ToLowerInvariant();
        return normalized == JpgAlias ? "image/jpeg" : normalized;
    }

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

    /// <summary>La extensión del nombre está permitida y el tipo MIME (normalizado) es el de esa extensión.</summary>
    public static bool IsAllowedType(string? name, string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        return AllowedMimeTypesByExtension.TryGetValue(Path.GetExtension(name.Trim()), out var allowed)
            && allowed.Contains(NormalizeMimeType(mimeType), StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsValidSize(long sizeBytes) => sizeBytes is > 0 and <= MaxSizeBytes;
}
