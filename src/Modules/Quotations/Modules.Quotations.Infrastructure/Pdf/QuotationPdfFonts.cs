using System.Reflection;
using PdfSharp.Fonts;

namespace Modules.Quotations.Infrastructure.Pdf;

/// <summary>
/// La tipografía del PDF, embebida en el ensamblado.
///
/// PDFsharp no trae fuentes: en Windows resuelve contra las instaladas, y en Linux —que es donde
/// corre esto (<c>mcr.microsoft.com/dotnet/aspnet</c>, sin ninguna fuente)— falla al primer
/// <c>XFont</c> si nadie le da un resolutor. Embeberlas es lo que hace que el documento salga
/// igual en la máquina de quien desarrolla, en las pruebas y en producción, sin depender de qué
/// tenga instalado el sistema ni de un paquete extra en la imagen.
///
/// Liberation Sans y no otra: es métricamente compatible con Helvetica, la fuente que usaba el
/// generador anterior (jsPDF, en el navegador). Mismos anchos de glifo, así que los cortes de
/// línea y las columnas caen donde caían. Se distribuye bajo SIL Open Font License 1.1 — ver
/// <c>Fonts/LICENSE.txt</c>, que viaja al lado de los archivos como la licencia exige.
/// </summary>
internal sealed class QuotationPdfFonts : IFontResolver
{
    internal const string FamilyName = "Liberation Sans";

    private const string RegularFaceName = "LiberationSans#Regular";
    private const string BoldFaceName = "LiberationSans#Bold";

    private static readonly Lazy<byte[]> Regular =
        new(() => Load("LiberationSans-Regular.ttf"));

    private static readonly Lazy<byte[]> Bold =
        new(() => Load("LiberationSans-Bold.ttf"));

    /// <summary>
    /// <c>GlobalFontSettings.FontResolver</c> es estático y de una sola escritura: PDFsharp tira
    /// si se lo cambia después de haber resuelto una fuente. Registrar acá, una vez, evita que
    /// dos renders en paralelo compitan por asignarlo.
    /// </summary>
    internal static void EnsureRegistered()
    {
        if (GlobalFontSettings.FontResolver is QuotationPdfFonts)
        {
            return;
        }

        GlobalFontSettings.FontResolver = new QuotationPdfFonts();
    }

    public byte[]? GetFont(string faceName) =>
        faceName == BoldFaceName ? Bold.Value : Regular.Value;

    // Cualquier familia cae en Liberation Sans: el documento usa una sola, y contestar null
    // dejaría a PDFsharp sin fuente con la que dibujar.
    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldFaceName : RegularFaceName);

    private static byte[] Load(string fileName)
    {
        var assembly = typeof(QuotationPdfFonts).GetTypeInfo().Assembly;
        var resourceName = $"{typeof(QuotationPdfFonts).Namespace}.Fonts.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The embedded font '{resourceName}' was not found in the assembly.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
