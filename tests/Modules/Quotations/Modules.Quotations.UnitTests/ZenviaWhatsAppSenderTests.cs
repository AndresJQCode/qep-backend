using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;
using Modules.Quotations.Infrastructure.Whatsapp;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El contrato con Zenvia vive en la forma del JSON, no en el status HTTP: una clave mal
/// nombrada entrega un mensaje sin PDF y con las variables vacías, y Zenvia igual responde 200.
/// Por eso estas pruebas afirman sobre el cuerpo que sale, no sobre el resultado del envío.
/// </summary>
public sealed class ZenviaWhatsAppSenderTests
{
    // Deliberadamente ficticios: lo que esta prueba verifica es que sale lo que se configuro,
    // no cual es la plantilla vigente. Poner acá el id real de `Quotations:WhatsApp:TemplateId`
    // obligaria a tocar el test cada vez que se aprueba una plantilla nueva -- que es siempre,
    // porque Meta no deja editar una aprobada.
    private const string TemplateId = "plantilla-de-prueba";
    private const string FromNumber = "570000000000";

    private static readonly WhatsAppQuotationMessage Message = new(
        ToPhone: "3001234567",
        FullName: "Juan Pérez",
        OrderNumber: "COT-000123",
        Total: 2450000m,
        ValidUntil: new DateOnly(2026, 9, 30),
        DocumentUrl: "https://r2.example.com/cotizacion.pdf?sig=abc");

    [Fact]
    public async Task SendPutsThePdfUrlInTheDocumentUrlField()
    {
        var (sender, capture, _) = NewSender();

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://r2.example.com/cotizacion.pdf?sig=abc",
            capture.Fields().GetProperty("documentUrl").GetString());
    }

    [Fact]
    public async Task SendFormatsTheTotalAsColombianPesos()
    {
        var (sender, capture, _) = NewSender();

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        var total = capture.Fields().GetProperty("total").GetString();
        Assert.NotNull(total);
        Assert.Contains("2.450.000", total);
        // Sin centavos: en es-CO el separador decimal es la coma, y un precio de cotización
        // redondo con ",00" al final sólo agrega ruido en un mensaje de WhatsApp.
        Assert.DoesNotContain(",", total);
    }

    [Fact]
    public async Task SendFormatsTheValidityDateInSpanish()
    {
        var (sender, capture, _) = NewSender();

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Equal(
            "30 de septiembre de 2026",
            capture.Fields().GetProperty("valid_until").GetString());
    }

    [Fact]
    public async Task SendUsesTheConfiguredTemplateAndSender()
    {
        var (sender, capture, _) = NewSender();

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        var body = capture.Body();
        Assert.Equal(FromNumber, body.GetProperty("from").GetString());
        Assert.Equal(
            TemplateId, body.GetProperty("contents")[0].GetProperty("templateId").GetString());
        Assert.Equal("token-de-prueba", capture.ApiToken);
    }

    // Customer.Phone es texto libre: un número local de 10 dígitos se envía con el indicativo.
    [Theory]
    [InlineData("3001234567", "573001234567")]
    [InlineData("+57 300 123 4567", "573001234567")]
    public async Task SendNormalizesTheRecipientPhone(string stored, string expected)
    {
        var (sender, capture, _) = NewSender();

        await sender.SendQuotationAsync(
            Message with { ToPhone = stored }, TestContext.Current.CancellationToken);

        Assert.Equal(expected, capture.Body().GetProperty("to").GetString());
    }

    [Fact]
    public async Task SendRejectsACustomerWithoutAPhone()
    {
        var (sender, _, _) = NewSender();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(
                Message with { ToPhone = null }, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.recipient_missing", error.Code);
    }

    [Fact]
    public async Task SendSurfacesAZenviaRejectionAsADomainError()
    {
        var (sender, _, _) = NewSender(HttpStatusCode.BadRequest, """{"code":"INVALID_TEMPLATE"}""");

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken));

        Assert.Equal("quotation.whatsapp.send_failed", error.Code);
        Assert.Contains("INVALID_TEMPLATE", error.Message);
    }

    // Zenvia responde 200 en cuanto encola el request: la entrega real ocurre después, del lado
    // de Meta, y sólo se puede rastrear por el id que devuelve en el cuerpo. Sin registrarlo no
    // queda forma de averiguar por qué una cotización marcada como enviada nunca llegó.
    [Fact]
    public async Task SendLogsTheZenviaMessageIdOnSuccess()
    {
        var (sender, _, logger) = NewSender(responseBody: """{"id":"zid-abc-123"}""");

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, message => message.Contains("zid-abc-123"));
    }

    // Un 2xx con un cuerpo que no trae `id` sigue siendo un envío aceptado: perder el registro
    // entero —y con él, la única huella de que la cotización salió— sería peor que anotarlo
    // sin identificador.
    [Fact]
    public async Task SendStillLogsTheAcceptanceWhenZenviaOmitsTheMessageId()
    {
        var (sender, _, logger) = NewSender(responseBody: """{"status":"queued"}""");

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, message => message.Contains("COT-000123"));
    }

    [Fact]
    public async Task SendStillLogsTheAcceptanceWhenZenviaAnswersWithoutJson()
    {
        var (sender, _, logger) = NewSender(responseBody: "OK");

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.Contains(logger.Messages, message => message.Contains("COT-000123"));
    }

    // Red de contención, no un ciclo rojo: el log ya nace sin estos datos. Existe para que
    // agregarlos más adelante —que es la tentación evidente cuando alguien vuelva a depurar un
    // "no me llegó"— rompa la prueba en vez de filtrar. La URL prefirmada da acceso al PDF del
    // tenant durante 24 horas y el teléfono es dato personal; un log se copia a soporte, a un
    // ticket o a una captura.
    [Fact]
    public async Task SendNeverLogsThePresignedUrlOrTheRecipientPhone()
    {
        var (sender, _, logger) = NewSender(responseBody: """{"id":"zid-abc-123"}""");

        await sender.SendQuotationAsync(Message, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.Messages, message => message.Contains("sig=abc"));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("3001234567"));
    }

    private static (IWhatsAppSender Sender, RequestCapture Capture, RecordingLogger Logger)
        NewSender(HttpStatusCode status = HttpStatusCode.OK, string responseBody = "{}")
    {
        var capture = new RequestCapture();
        var logger = new RecordingLogger();
        var options = Options.Create(new QuotationsOptions
        {
            WhatsApp = new WhatsAppOptions
            {
                ApiToken = "token-de-prueba",
                FromNumber = FromNumber,
                TemplateId = TemplateId,
                BaseUrl = "https://api.zenvia.com",
            },
        });

        return (
            new ZenviaWhatsAppSender(
                new HttpClient(new CapturingHandler(capture, status, responseBody)),
                options,
                logger),
            capture,
            logger);
    }

    private sealed class RequestCapture
    {
        public string Json { get; set; } = "{}";

        public string? ApiToken { get; set; }

        public JsonElement Body() => JsonDocument.Parse(Json).RootElement;

        public JsonElement Fields() =>
            Body().GetProperty("contents")[0].GetProperty("fields");
    }

    private sealed class CapturingHandler(
        RequestCapture capture, HttpStatusCode status, string responseBody)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture.Json = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            capture.ApiToken = request.Headers.TryGetValues("X-API-TOKEN", out var values)
                ? values.FirstOrDefault()
                : null;

            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class RecordingLogger : ILogger<ZenviaWhatsAppSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
