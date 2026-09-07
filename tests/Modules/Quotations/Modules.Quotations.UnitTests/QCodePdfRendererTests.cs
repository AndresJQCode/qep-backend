using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;
using Modules.Quotations.Infrastructure.Pdf;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// `qcode-pdf` es un servicio genérico: no conoce la cotización. El markup Typst viaja entero
/// en cada request y los datos van aparte, así que lo que estas pruebas cuidan es el contrato
/// con ese servicio — que el documento llegue como `data`, que la key viaje, y que un markup
/// que no compila se convierta en un error de dominio y no en un PDF vacío.
/// </summary>
public sealed class QCodePdfRendererTests
{
    private static readonly QuotationPdfDocument Document = new(
        QuotationNumber: "QUO-2026-0002",
        CreatedAt: new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero),
        ValidUntil: new DateOnly(2026, 9, 30),
        CustomerName: "Comercializadora del Norte S.A.S.",
        CustomerCuc: "CUC-0042",
        CustomerContact: "3001234567 · compras@ejemplo.co",
        CustomerLocation: "Calle 100 #15-20, Bogotá",
        Billing: new QuotationPdfParty(true, "", "", ""),
        Shipping: new QuotationPdfParty(true, "", "", ""),
        AdvisorLabel: "Ana Pérez",
        Currency: "COP",
        BillingAccount: null,
        PaymentMethod: "Transferencia",
        Notes: null,
        Items:
        [
            new QuotationPdfLine("Tornillo hexagonal 3/8", 100m, 1500m, 0m, 150000m),
        ],
        Subtotal: 150000m,
        DiscountAmount: 0m,
        TaxPercentage: 19m,
        TaxAmount: 28500m,
        Total: 178500m);

    [Fact]
    public async Task RenderReturnsThePdfBytesTheServiceProduces()
    {
        var (renderer, _) = NewRenderer(pdf: [0x25, 0x50, 0x44, 0x46]);

        var pdf = await renderer.RenderAsync(Document, TestContext.Current.CancellationToken);

        Assert.Equal([0x25, 0x50, 0x44, 0x46], pdf);
    }

    [Fact]
    public async Task RenderSendsTheDocumentAsDataAndTheTemplateAsSource()
    {
        var (renderer, capture) = NewRenderer();

        await renderer.RenderAsync(Document, TestContext.Current.CancellationToken);

        var body = capture.Body();
        Assert.False(
            string.IsNullOrWhiteSpace(body.GetProperty("source").GetString()),
            "el markup Typst tiene que viajar en cada request: el servicio no guarda plantillas");
        Assert.Equal(
            "QUO-2026-0002",
            body.GetProperty("data").GetProperty("quotationNumber").GetString());
    }

    [Fact]
    public async Task RenderAuthenticatesWithTheConfiguredApiKey()
    {
        var (renderer, capture) = NewRenderer();

        await renderer.RenderAsync(Document, TestContext.Current.CancellationToken);

        Assert.Equal("clave-de-prueba", capture.ApiKey);
    }

    // El servicio responde 400 con { error, details } cuando el markup no compila. Sin esto el
    // caso de uso seguiria adelante con un PDF vacio y el cliente recibiria un adjunto roto.
    [Fact]
    public async Task RenderSurfacesACompileErrorAsADomainError()
    {
        var (renderer, _) = NewRenderer(
            status: HttpStatusCode.BadRequest,
            responseBody: """{"error":"Error compilando documento Typst","details":"unknown variable"}""");

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            renderer.RenderAsync(Document, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.pdf.render_failed", error.Code);
        Assert.Contains("unknown variable", error.Message);
    }

    private static (IQuotationPdfRenderer Renderer, RequestCapture Capture) NewRenderer(
        byte[]? pdf = null,
        HttpStatusCode status = HttpStatusCode.OK,
        string? responseBody = null)
    {
        var capture = new RequestCapture();
        var options = Options.Create(new QuotationsOptions
        {
            Pdf = new PdfOptions
            {
                BaseUrl = "https://qcode-pdf.qcode.co",
                ApiKey = "clave-de-prueba",
            },
        });

        return (
            new QCodePdfRenderer(
                new HttpClient(new CapturingHandler(capture, status, pdf ?? [0x25], responseBody)),
                options),
            capture);
    }

    private sealed class RequestCapture
    {
        public string Json { get; set; } = "{}";

        public string? ApiKey { get; set; }

        public JsonElement Body() => JsonDocument.Parse(Json).RootElement;
    }

    private sealed class CapturingHandler(
        RequestCapture capture, HttpStatusCode status, byte[] pdf, string? responseBody)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture.Json = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            capture.ApiKey = request.Headers.TryGetValues("X-Api-Key", out var values)
                ? values.FirstOrDefault()
                : null;

            return new HttpResponseMessage(status)
            {
                Content = responseBody is null
                    ? new ByteArrayContent(pdf)
                    : new StringContent(responseBody),
            };
        }
    }
}
