using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Envía la cotización por WhatsApp vía la API de plantillas de Zenvia
/// (`POST /v2/channels/whatsapp/messages`, `curl` de referencia del owner). Mismo criterio de
/// implementación que `InfobipEmailChannel` en Notifications: un `HttpClient` sencillo, sin
/// `IHttpClientFactory` — este módulo tampoco lo usa en ningún otro lado.
///
/// Ya no lee `Quotations:WhatsApp:*` por su cuenta (spec 2026-10-07): recibe la cuenta con la que
/// sale el envío como <see cref="ZenviaSenderSettings"/>. `AddWhatsAppSender` lo arma para la cuenta
/// de QEP (sólo si las tres claves están presentes; `LogWhatsAppSender` es el default en su
/// ausencia) y `WhatsAppChannelResolver` lo arma por envío para una cuenta propia.
/// </summary>
internal sealed partial class ZenviaWhatsAppSender(
    HttpClient httpClient,
    ZenviaSenderSettings settings,
    ILogger<ZenviaWhatsAppSender> logger)
    : IWhatsAppSender
{
    // El formato con el que el cliente ve el monto y la vigencia lo fija el locale de la
    // plantilla (`es` en Zenvia), no el contrato de Application — por eso se arma acá.
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    private const string UnknownMessageId = "(unknown)";

    // Spec 2026-10-07: el código de error de Zenvia sólo entra al mensaje si tiene esta forma. El
    // patrón impide que un texto libre del cuerpo se cuele por ese campo. Cierra con `\z` y no con `$`,
    // porque `$` también acepta un salto de línea final.
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ZenviaErrorCodePattern();

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
            throw Failure(response.StatusCode, body);
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

    /// <summary>
    /// Cuenta de QEP: como siempre, <c>send_failed</c> con el cuerpo (decisión 14 del spec).
    /// Cuenta propia: 401/403 son <c>credentials_rejected</c> —lo único que el administrador puede
    /// arreglar desde Configuración— y nunca el cuerpo crudo, que podría devolver lo que la persona
    /// pegó: sólo el estado y, si cumple el patrón, el código de Zenvia.
    /// </summary>
    private QuotationsDomainException Failure(HttpStatusCode status, string body)
    {
        var statusCode = (int)status;
        if (settings.Account == ZenviaAccount.Qep)
        {
            return new QuotationsDomainException(
                "quotation.whatsapp.send_failed", $"Zenvia responded {statusCode}: {body}");
        }

        var zenviaCode = ReadErrorCode(body);
        var message = zenviaCode is null
            ? $"Zenvia responded {statusCode}."
            : $"Zenvia responded {statusCode} (code: {zenviaCode}).";

        return status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new QuotationsDomainException("quotation.whatsapp.credentials_rejected", message)
            : new QuotationsDomainException("quotation.whatsapp.send_failed", message);
    }

    // Tolerante, como ReadMessageId: la forma de error de Zenvia no está verificada en el código.
    private static string? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } value
                && ZenviaErrorCodePattern().IsMatch(value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
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
