using System.IO.Compression;
using System.Text;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Scanning;

namespace Modules.Storage.UnitTests;

public sealed class FileUploadPolicyTests
{
    [Theory]
    [InlineData("file.pdf", "application/pdf")]
    [InlineData("file.doc", "application/msword")]
    [InlineData("file.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("file.xls", "application/vnd.ms-excel")]
    [InlineData("file.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("file.jpg", "image/jpeg")]
    [InlineData("file.jpeg", "image/jpeg")]
    [InlineData("file.webp", "image/webp")]
    [InlineData("file.png", "image/png")]
    public void AllowedDeclarationIsAccepted(string name, string mimeType) =>
        FileUploadPolicy.ValidateDeclaration(name, mimeType, 1024);

    [Theory]
    [InlineData("malware.exe", "application/octet-stream")]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("image.jpg", "application/pdf")]
    public void InvalidDeclarationIsRejected(string name, string mimeType) =>
        Assert.Throws<StorageDomainException>(
            () => FileUploadPolicy.ValidateDeclaration(name, mimeType, 1024));

    // Spec 2026-11-18: el multimedia de un chatbot acepta los formatos que el bot manda por WhatsApp.
    [Theory]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.3gp", "video/3gpp")]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.m4a", "audio/mp4")]
    [InlineData("a.aac", "audio/aac")]
    [InlineData("a.amr", "audio/amr")]
    [InlineData("a.ogg", "audio/ogg")]
    public void ChatbotMediaAcceptsEveryOutboundMediaFormat(string name, string mimeType) =>
        FileUploadPolicy.ValidateDeclaration(name, mimeType, 1024, 16L * 1024 * 1024, FileOwnerType.ChatbotMedia);

    // La consola no cambia: video y audio siguen fuera de su tabla.
    [Fact]
    public void TheConsoleStillRejectsVideo()
    {
        var error = Assert.Throws<StorageDomainException>(
            () => FileUploadPolicy.ValidateDeclaration("a.mp4", "video/mp4", 10, FileUploadPolicy.MaxSizeBytes));

        Assert.Equal("storage.file.type_not_allowed", error.Code);
    }

    // El tope de 100 MB es del dueño ChatbotMedia; para cualquier otro sigue siendo un error de programación.
    [Fact]
    public void ChatbotMediaAllowsAHundredMegabyteDocument()
    {
        const long hundredMegabytes = 100L * 1024 * 1024;

        FileUploadPolicy.ValidateDeclaration(
            "a.pdf", "application/pdf", hundredMegabytes, hundredMegabytes, FileOwnerType.ChatbotMedia);
        Assert.Throws<ArgumentOutOfRangeException>(() => FileUploadPolicy.ValidateDeclaration(
            "a.pdf", "application/pdf", hundredMegabytes, hundredMegabytes, FileOwnerType.Product));
    }

    [Fact]
    public void InspectorRejectsSpoofedPdf()
    {
        var inspector = new FileContentInspector();
        Assert.False(inspector.Matches(
            "invoice.pdf", "application/pdf", "not a pdf"u8.ToArray()));
    }

    [Fact]
    public void InspectorDistinguishesWordAndExcelOpenXml()
    {
        var inspector = new FileContentInspector();
        var document = CreateOpenXml("word/document.xml");

        Assert.True(inspector.Matches(
            "document.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            document));
        Assert.False(inspector.Matches(
            "document.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            document));
    }

    private static byte[] CreateOpenXml(string partName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", "<Types />");
            Write(archive, partName, "<document />");
        }
        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(value);
    }
}
