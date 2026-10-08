using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Envía la cotización por WhatsApp vía la API de plantillas de Zenvia
/// (`POST /v2/channels/whatsapp/messages`, `curl` de referencia del owner). Mismo criterio de
/// implementación que `InfobipEmailChannel` en Notifications: un `HttpClient` sencillo, sin
/// `IHttpClientFactory` — este módulo tampoco lo usa en ningún otro lado.
///
/// Sólo se registra cuando `Quotations:WhatsApp:ApiToken`/`FromNumber`/`TemplateId` están
/// las tres presentes (`QuotationsInfrastructureExtensions.AddWhatsAppSender`) — `LogWhatsAppSender`
/// es el default en su ausencia, así que acá adentro esas tres claves ya se asumen no vacías.
/// </summary>
internal sealed partial class ZenviaWhatsAppSender(
    HttpClient httpClient,
    IOptions<QuotationsOptions> options,
    ILogger<ZenviaWhatsAppSender> logger)
    : IWhatsAppSender
{
    // El formato con el que el cliente ve el monto y la vigencia lo fija el locale de la
    // plantilla (`es` en Zenvia), no el contrato de Application — por eso se arma acá.
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    private const string UnknownMessageId = "(unknown)";

    private readonly WhatsAppOptions settings = options.Value.WhatsApp;

    // "Accepted", no "sent": un 2xx significa que Zenvia encoló el mensaje, y la entrega la
    // resuelve Meta después, de forma asíncrona. El {MessageId} es el único hilo que conecta
    // este envío con su estado real en la consola de Zenvia; sin él, un "no me llegó" no se
    // puede rastrear. Ni el teléfono del destinatario ni la URL prefirmada del PDF entran al
    // log: el primero es dato personal y la segunda es una credencial de acceso al archivo.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WhatsApp quotation {OrderNumber} accepted by Zenvia as message {MessageId}.")]
    private static partial void LogAccepted(
        ILogger logger, string orderNumber, string messageId);

    public async Task SendQuotationAsync(
        WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        var to = NormalizePhone(message.ToPhone);
        if (to is null)
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp.recipient_missing",
                "The client has no phone number to send the quotation to.");
        }

        var payload = new
        {
            from = settings.FromNumber,
            to,
            contents = new object[]
            {
                new
                {
                    type = "template",
                    templateId = settings.TemplateId,
                    fields = new
                    {
                        fullname = message.FullName,
                        order_number = message.OrderNumber,
                        total = message.Total.ToString("C0", Colombia),
                        valid_until = message.ValidUntil.ToString(
                            "d 'de' MMMM 'de' yyyy", Colombia),
                        // La clave se llama `documentUrl` porque así la nombra Zenvia para los
                        // templates con media (`imageUrl`/`videoUrl` para los otros): no hay un
                        // contenido aparte de tipo `file`, el adjunto es una variable más.
                        documentUrl = message.DocumentUrl,
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{settings.BaseUrl.TrimEnd('/')}/v2/channels/whatsapp/messages")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-API-TOKEN", settings.ApiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp.send_failed",
                $"Zenvia responded {(int)response.StatusCode}: {body}");
        }

        // El guard es por CA1873: `ReadMessageId` parsea el cuerpo, y el `LoggerMessage`
        // generado sólo descartaría el resultado después de calcularlo.
        if (logger.IsEnabled(LogLevel.Information))
        {
            var messageId = ReadMessageId(body);
            LogAccepted(logger, message.OrderNumber, messageId);
        }
    }

    // Tolerante a propósito: un 2xx sin un `id` reconocible sigue siendo un envío aceptado, y
    // perder el registro entero por un cuerpo con otra forma sería peor que anotarlo sin
    // identificador.
    private static string ReadMessageId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("id", out var id)
                ? id.ToString()
                : UnknownMessageId;
        }
        catch (JsonException)
        {
            return UnknownMessageId;
        }
    }

    // Mismo criterio que `buildWhatsAppLink` en el frontend (`whatsapp-link.ts`, ahora
    // retirado): `Customer.Phone` es texto libre sin indicativo obligatorio — un número de 10
    // dígitos se asume local (Colombia) y se le antepone 57; uno más largo se asume que ya lo
    // trae.
    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return null;

        return digits.Length == 10 ? $"57{digits}" : digits;
    }
}
