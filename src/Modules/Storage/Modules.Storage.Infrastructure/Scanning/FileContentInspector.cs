using System.IO.Compression;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Scanning;

internal sealed class FileContentInspector : IFileContentInspector
{
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public bool Matches(string name, string mimeType, byte[] content)
    {
        if (content.Length == 0)
        {
            return false;
        }

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" => StartsWith(content, Pdf),
            ".png" => StartsWith(content, Png),
            ".jpg" or ".jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            ".webp" => content.Length >= 12 &&
                content.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                content.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            ".doc" or ".xls" => StartsWith(content, Ole),
            ".docx" => IsOpenXml(content, "word/"),
            ".xlsx" => IsOpenXml(content, "xl/"),
            _ => false,
        };
    }

    private static bool StartsWith(byte[] content, byte[] signature) =>
        content.Length >= signature.Length && content.AsSpan(0, signature.Length).SequenceEqual(signature);

    private static bool IsOpenXml(byte[] content, string requiredPrefix)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var hasContentTypes = archive.Entries.Any(entry =>
                string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            var hasRequiredPart = archive.Entries.Any(entry =>
                entry.FullName.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase));
            return hasContentTypes && hasRequiredPart;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
