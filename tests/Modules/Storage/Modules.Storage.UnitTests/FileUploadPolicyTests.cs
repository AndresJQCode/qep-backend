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
