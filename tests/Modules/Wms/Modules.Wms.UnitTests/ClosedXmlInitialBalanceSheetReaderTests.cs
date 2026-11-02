using System.IO.Compression;
using ClosedXML.Excel;
using Modules.Wms.Domain;
using Modules.Wms.Infrastructure.Excel;

namespace Modules.Wms.UnitTests;

/// <summary>El lector de la carga inicial no le entrega a ClosedXML nada que no haya medido antes: un .xlsx de 1 MB comprimido puede
/// ocupar más de 1,5 GB al abrirse, y con un solo pod eso tumba a todos los tenants.</summary>
public sealed class ClosedXmlInitialBalanceSheetReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClosedXmlInitialBalanceSheetReader Reader = new();

    private static byte[] Zip(int entryBytes)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize);
            using var writer = entry.Open();
            writer.Write(new byte[entryBytes]);
        }

        return stream.ToArray();
    }

    private static void ParseThrowsInvalidFile(byte[] file) =>
        Assert.Equal(
            WmsErrorCodes.ImportInvalidFile,
            Assert.Throws<WmsDomainException>(() => Reader.Parse(new MemoryStream(file), Ct)).Code);

    [Fact]
    public void ADeclaredUncompressedSizeOverTheLimitIsInvalidFile() =>
        ParseThrowsInvalidFile(Zip(ClosedXmlInitialBalanceSheetReader.MaxUncompressedBytes + 1024));

    [Fact]
    public void AZipWithTooManyEntriesIsInvalidFile()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index <= ClosedXmlInitialBalanceSheetReader.MaxEntries; index++)
            {
                zip.CreateEntry($"xl/part{index}.xml");
            }
        }

        ParseThrowsInvalidFile(stream.ToArray());
    }

    [Fact]
    public void AnUploadOverTheCompressedLimitIsInvalidFile() =>
        ParseThrowsInvalidFile(new byte[ClosedXmlInitialBalanceSheetReader.MaxCompressedBytes + 1]);

    [Fact]
    public void ABytesStreamThatIsNotAZipIsInvalidFile() => ParseThrowsInvalidFile("esto no es un xlsx"u8.ToArray());

    // Los tamaños declarados se pueden falsear: se parchean el directorio central y el encabezado local para que digan 100 bytes. Lo que
    // cuenta es lo que de verdad sale al descomprimir, y nunca puede terminar en una hoja leída.
    [Fact]
    public void ALyingCentralDirectoryNeverEndsInAParsedSheet()
    {
        var file = Zip(ClosedXmlInitialBalanceSheetReader.MaxUncompressedBytes + 1024);
        PatchUncompressedSize(file, signature: [0x50, 0x4B, 0x01, 0x02], offset: 24);
        PatchUncompressedSize(file, signature: [0x50, 0x4B, 0x03, 0x04], offset: 22);

        try
        {
            var sheet = Reader.Parse(new MemoryStream(file), Ct);
            Assert.False(sheet.HasExpectedColumns);
        }
        catch (WmsDomainException error)
        {
            Assert.Equal(WmsErrorCodes.ImportInvalidFile, error.Code);
        }
    }

    private static void PatchUncompressedSize(byte[] file, byte[] signature, int offset)
    {
        var at = file.AsSpan().IndexOf(signature);
        Assert.True(at >= 0, "signature not found");
        BitConverter.GetBytes(100).CopyTo(file, at + offset);
    }

    [Fact]
    public void AValidWorkbookParsesAndANumberOutOfDecimalRangeStaysARowNotAFile()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Saldos");
        string[] headers = ["code", "location", "lot", "expiration", "quantity", "uom"];
        for (var column = 0; column < headers.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
        }

        sheet.Cell(2, 1).Value = "V1";
        sheet.Cell(2, 2).Value = "A-01";
        sheet.Cell(2, 5).Value = 1e30;
        sheet.Cell(3, 1).Value = "V2";
        sheet.Cell(3, 2).Value = "A-01";
        sheet.Cell(3, 5).Value = 12.5;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var parsed = Reader.Parse(new MemoryStream(stream.ToArray()), Ct);

        Assert.True(parsed.HasExpectedColumns);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("12.5", parsed.Rows[1].Quantity);
        Assert.False(decimal.TryParse(parsed.Rows[0].Quantity, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _));
    }
}
