using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure;
using Modules.Quotations.Infrastructure.Pdf;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El agujero que estas pruebas tapan: la plantilla `quotation.typ` **no la compila nadie** en el
/// camino de `dotnet build` ni de `dotnet test`. Es un recurso embebido que viaja como texto, así
/// que romperla —renombrar un campo del DTO, equivocarse en la sintaxis— deja el build verde, las
/// pruebas verdes, y falla recién en produccion cuando un asesor le da a "enviar": el servicio
/// responde 400 y el caso de uso tira `quotation.pdf.render_failed`.
///
/// Ya paso una vez con los importes de linea. La diferencia entre "el documento dice un numero
/// mal" y "el documento no existe" es de grado, no de clase: las dos las descubre el cliente.
///
/// Todo lo que se verifica sale del **mismo request** que `QCodePdfRenderer` le manda al servicio
/// —`source` y `data` capturados de la llamada real— y no de una copia. Si se reconstruyera la
/// plantilla o la serializacion por separado, la prueba podria pasar mientras produccion falla,
/// que es exactamente el problema que viene a resolver.
/// </summary>
public sealed partial class QuotationTemplateTests
{
    // Los tres nombres con los que la plantilla desreferencia el payload: la raiz, la variable de
    // cada linea, y la de cada parte (`parte(data.billing)`). Un binding nuevo que no figure aca
    // no se verifica -- es un hueco de cobertura, no un falso rojo, y por eso vive a la vista.
    private static readonly string[] Bindings = ["data", "item", "valor"];

    /// <summary>
    /// El contrato entre el DTO y la plantilla, que hoy solo existe como un comentario en
    /// <c>QCodePdfRenderer</c>: "cambiarlo aca rompe el `.typ` sin que falle la compilacion de
    /// C#". Cada campo que la plantilla lee tiene que estar en el JSON que viaja al lado.
    ///
    /// No necesita `typst` instalado: corre siempre, en cualquier maquina y en CI.
    /// </summary>
    [Fact]
    public async Task EveryFieldTheTemplateReadsExistsInThePayload()
    {
        var (source, data) = await CapturedRequestAsync(Complete());

        // Los comentarios del `.typ` nombran campos a proposito ("la plantilla lee
        // data.quotationNumber"), y un ejemplo en un comentario no es un campo que haga falta.
        var markup = string.Join(
            '\n',
            source.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

        var checkedPaths = 0;
        foreach (var reference in Reference().Matches(markup).Cast<Match>())
        {
            var binding = reference.Groups[1].Value;
            if (!Bindings.Contains(binding)) continue;

            AssertPathExists(RootFor(binding, data), binding, reference.Groups[2].Value);
            checkedPaths++;
        }

        // Sin esto la prueba pasaria en verde si la expresion dejara de encontrar nada.
        Assert.True(checkedPaths > 20, $"solo se verificaron {checkedPaths} referencias");
    }

    // El caso completo ejercita todas las ramas que dibujan algo: descuento por linea, retencion,
    // cuenta de cobro, partes con datos propios y observaciones.
    [Fact]
    public async Task TheTemplateCompilesWithACompleteDocument() =>
        await AssertCompilesAsync(Complete());

    // Y el caso vacio ejercita las que **no** dibujan. Es donde una plantilla se rompe de verdad:
    // `data.billingAccount.companyName` revienta si nadie pregunto por el `none` de arriba.
    [Fact]
    public async Task TheTemplateCompilesWithEverythingOptionalMissing() =>
        await AssertCompilesAsync(Minimal());

    // ------------------------------------------------------------------ compilacion

    private static async Task AssertCompilesAsync(QuotationPdfDocument document)
    {
        var typst = FindTypst();
        if (typst is null)
        {
            // En CI la ausencia del binario no puede ser un skip: seria el mismo agujero con otra
            // forma, y en verde. El workflow instala la version clavada en el contenedor.
            Assert.SkipWhen(
                !RunningOnCi,
                "`typst` no esta en PATH ni en TYPST_BIN. Instalalo para ejercer esta prueba: " +
                "https://github.com/typst/typst/releases");

            Assert.Fail("`typst` tiene que estar instalado en CI para compilar la plantilla.");
        }

        var (source, data) = await CapturedRequestAsync(document);

        // Mismo layout que arma `qcode-pdf`: la plantilla y su `data.json` en el mismo directorio,
        // porque la ruta de `--input` se resuelve relativa al `.typ` y no al directorio de trabajo.
        var workspace = Directory.CreateTempSubdirectory("qep-typst");
        try
        {
            var template = Path.Combine(workspace.FullName, "quotation.typ");
            await File.WriteAllTextAsync(
                template, source, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workspace.FullName, "data.json"),
                data.GetRawText(),
                TestContext.Current.CancellationToken);

            var output = Path.Combine(workspace.FullName, "quotation.pdf");
            var (exitCode, diagnostics) = await RunAsync(
                typst!, workspace.FullName, ["compile", "--input", "data=data.json", template, output]);

            Assert.True(exitCode == 0, $"typst no pudo compilar la plantilla:\n{diagnostics}");
            Assert.True(File.Exists(output), "typst devolvio 0 pero no dejo el PDF.");

            var bytes = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal("%PDF"u8.ToArray(), bytes[..4]);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Diagnostics)> RunAsync(
        string executable, string workingDirectory, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"No se pudo iniciar {executable}.");

        // `typst` avisa por `warning` cuando una fuente no existe y cae a la serif por defecto,
        // asi que la salida de error importa incluso con exit 0.
        var diagnostics = await process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, diagnostics);
    }

    // `TYPST_BIN` es la misma variable que el `Dockerfile` de `qcode-pdf` le fija al servicio.
    private static string? FindTypst()
    {
        var configured = Environment.GetEnvironmentVariable("TYPST_BIN");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var name = OperatingSystem.IsWindows() ? "typst.exe" : "typst";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Una entrada de PATH con caracteres invalidos no es motivo para no seguir buscando.
            }
        }

        return null;
    }

    private static bool RunningOnCi =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    // ------------------------------------------------------------------ contrato

    [GeneratedRegex(@"\b([A-Za-z][A-Za-z0-9]*)\.([A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*)*)")]
    private static partial Regex Reference();

    private static JsonElement RootFor(string binding, JsonElement data) => binding switch
    {
        "item" => data.GetProperty("items")[0],
        "valor" => data.GetProperty("billing"),
        _ => data,
    };

    /// <summary>
    /// Camina la ruta contra el JSON. En cuanto el nodo deja de ser un objeto, lo que sigue es un
    /// metodo de Typst y no un campo: `data.items.any(...)` es la coleccion `items` y el `.any` de
    /// la libreria, no un campo llamado "any".
    /// </summary>
    private static void AssertPathExists(JsonElement root, string binding, string path)
    {
        var current = root;
        var walked = binding;

        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return;

            Assert.True(
                current.TryGetProperty(segment, out var next),
                $"La plantilla lee `{walked}.{segment}` y el payload no lo trae. " +
                "Si se renombro un campo de QuotationPdfDocument, hay que renombrarlo tambien en " +
                "quotation.typ: el `.typ` no lo ve el compilador de C#.");

            walked = $"{walked}.{segment}";
            current = next;
        }
    }

    // ------------------------------------------------------------------ el request real

    private static async Task<(string Source, JsonElement Data)> CapturedRequestAsync(
        QuotationPdfDocument document)
    {
        var capture = new RequestCapture();
        var options = Options.Create(new QuotationsOptions
        {
            Pdf = new PdfOptions { BaseUrl = "https://qcode-pdf.qcode.co", ApiKey = "clave-de-prueba" },
        });

        var renderer = new QCodePdfRenderer(
            new HttpClient(new CapturingHandler(capture)), options);
        await renderer.RenderAsync(document, TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(capture.Json).RootElement;
        return (body.GetProperty("source").GetString()!, body.GetProperty("data"));
    }

    private sealed class RequestCapture
    {
        public string Json { get; set; } = "{}";
    }

    private sealed class CapturingHandler(RequestCapture capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture.Json = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x25, 0x50, 0x44, 0x46]),
            };
        }
    }

    // ------------------------------------------------------------------ documentos

    private static QuotationPdfDocument Complete() => new(
        QuotationNumber: "QUO-2026-0042",
        CreatedAt: new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero),
        ValidUntil: new DateOnly(2026, 9, 30),
        CustomerName: "Comercializadora del Norte S.A.S.",
        CustomerCuc: "CUC-0042",
        CustomerContact: "3001234567 · compras@ejemplo.co",
        CustomerLocation: "Calle 100 #15-20, Bogotá",
        Billing: new QuotationPdfParty(
            false, "Sede administrativa", "6015550198 · facturacion@ejemplo.co", "Carrera 7 #71-52"),
        Shipping: new QuotationPdfParty(
            false, "Bodega Fontibón", "3109987766 · bodega@ejemplo.co", "Zona Franca, Bodega 14"),
        AdvisorLabel: "ana.perez@ejemplo.co",
        Currency: "COP",
        BillingAccount: new QuotationPdfBillingAccount(
            "Ferretería Andina S.A.S.", "900.123.456-7", "Bancolombia", "123-456789-01", "COP"),
        PaymentMethod: "Transferencia bancaria a 30 días",
        Notes: "Los precios no incluyen transporte hasta la bodega del cliente.",
        Items:
        [
            new QuotationPdfLine("BRONCEADOR RITUAL DEL SOL", 12m, 35900m, 15m, 30515m, 366180m),
            new QuotationPdfLine("COMBO JALEA REAL 3", 12m, 114700m, 15m, 97495m, 1169940m),
        ],
        Subtotal: 1290857.15m,
        DiscountAmount: 271080m,
        TaxPercentage: 19m,
        TaxAmount: 245262.85m,
        Total: 1536120m,
        RetentionAmount: 32271.43m,
        NetTotal: 1503848.57m,
        CustomerVatSurplus: false);

    private static QuotationPdfDocument Minimal() => new(
        QuotationNumber: "QUO-2026-0002",
        CreatedAt: new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero),
        ValidUntil: null,
        CustomerName: string.Empty,
        CustomerCuc: string.Empty,
        CustomerContact: string.Empty,
        CustomerLocation: string.Empty,
        Billing: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        Shipping: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        AdvisorLabel: string.Empty,
        Currency: "USD",
        BillingAccount: null,
        PaymentMethod: null,
        Notes: null,
        Items: [new QuotationPdfLine("Tornillo hexagonal 3/8", 100m, 15.5m, 0m, 15.5m, 1550m)],
        Subtotal: 1302.52m,
        DiscountAmount: 0m,
        TaxPercentage: 19m,
        TaxAmount: 247.48m,
        Total: 1550m,
        RetentionAmount: 0m,
        NetTotal: 1550m,
        CustomerVatSurplus: true);
}
