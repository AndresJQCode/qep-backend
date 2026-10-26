using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// Envía la cotización por WhatsApp vía la API de plantillas de Zenvia
/// (`POST /v2/channels/whatsapp/messages`, `curl` de referencia del owner). Mismo criterio de
/// implementación que `InfobipEmailChannel` en Notifications: un `HttpClient` sencillo, sin
/// `IHttpClientFactory` — este módulo tampoco lo usa en ningún otro lado.
///
/// No guarda ninguna cuenta: el token, el número emisor y las plantillas llegan en cada envío como el
/// <see cref="WhatsAppChannel"/> del tenant (plan 2026-10-21; la cuenta global se retiró). De la
/// configuración sólo lee <c>Quotations:WhatsApp:BaseUrl</c>.
/// </summary>
internal sealed partial class ZenviaWhatsAppSender(
    HttpClient httpClient,
    IOptions<QuotationsOptions> options,
    ILogger<ZenviaWhatsAppSender> logger)
    : IZenviaWhatsAppSender
{
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WhatsApp order {OrderNumber} accepted by Zenvia as message {MessageId}.")]
    private static partial void LogOrderAccepted(
        ILogger logger, string orderNumber, string messageId);

    public async Task SendQuotationAsync(
        ZenviaWhatsAppChannel channel, WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        var to = WhatsAppPhone.Normalize(message.ToPhone);
        if (to is null)
        {
            throw new QuotationsDomainException(
                "quotation.whatsapp.recipient_missing",
                "The client has no phone number to send the quotation to.");
        }

        var documentUrl = RequireDocumentUrl(message.DocumentUrl);

        var payload = new
        {
            from = channel.FromNumber,
            to,
            contents = new object[]
            {
                new
                {
                    type = "template",
                    templateId = channel.QuotationTemplateId,
                    fields = new
                    {
                        fullname = message.FullName,
                        order_number = message.OrderNumber,
                        // The quotation currency's symbol and decimals (catalogue), the tenant's separators
                        // (Colombian when it has none): "€ 2.450,50" or "€ 2,450.50". The template variable is the
                        // same, so the approved template stays valid.
                        total = WhatsAppTemplateValues.Total(message.Total, message.Currency, message.NumberFormat),
                        valid_until = WhatsAppTemplateValues.ValidUntil(message.ValidUntil),
                        // La clave se llama `documentUrl` porque así la nombra Zenvia para los
                        // templates con media (`imageUrl`/`videoUrl` para los otros): no hay un
                        // contenido aparte de tipo `file`, el adjunto es una variable más.
                        documentUrl,
                    },
                },
            },
        };

        var body = await PostAsync(channel, payload, "quotation.whatsapp.send_failed", cancellationToken);

        // El guard es por CA1873: `ReadMessageId` parsea el cuerpo, y el `LoggerMessage`
        // generado sólo descartaría el resultado después de calcularlo.
        if (logger.IsEnabled(LogLevel.Information))
        {
            var messageId = ReadMessageId(body);
            LogAccepted(logger, message.OrderNumber, messageId);
        }
    }

    /// <summary>
    /// El pedido, por su propia plantilla (<see cref="ZenviaWhatsAppChannel.OrderTemplateId"/>). Mismo cuerpo
    /// que la cotización menos <c>valid_until</c>: un pedido no vence. Los códigos son de pedido
    /// (<c>order.whatsapp.*</c>) para que la pantalla del pedido no tenga que traducir los de la
    /// cotización.
    /// </summary>
    public async Task SendOrderAsync(
        ZenviaWhatsAppChannel channel, WhatsAppOrderMessage message, CancellationToken cancellationToken)
    {
        // Defensa: `SendOrderHandler` ya lo rechaza al elegir el canal, antes del PDF. Va antes que el
        // teléfono porque decir "falta el teléfono" mandaría a corregir un dato que no destraba nada.
        if (string.IsNullOrWhiteSpace(channel.OrderTemplateId))
        {
            throw new QuotationsDomainException(
                "order.whatsapp.not_configured",
                "The WhatsApp channel has no order template: orders cannot be sent by WhatsApp yet.");
        }

        var to = WhatsAppPhone.Normalize(message.ToPhone);
        if (to is null)
        {
            throw new QuotationsDomainException(
                "order.whatsapp.recipient_missing",
                "The recipient has no phone number to send the order to.");
        }

        var documentUrl = RequireDocumentUrl(message.DocumentUrl);

        var payload = new
        {
            from = channel.FromNumber,
            to,
            contents = new object[]
            {
                new
                {
                    type = "template",
                    templateId = channel.OrderTemplateId,
                    fields = new
                    {
                        fullname = message.FullName,
                        order_number = message.OrderNumber,
                        total = WhatsAppTemplateValues.Total(message.Total, message.Currency, message.NumberFormat),
                        documentUrl,
                    },
                },
            },
        };

        var body = await PostAsync(channel, payload, "order.whatsapp.send_failed", cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            var messageId = ReadMessageId(body);
            LogOrderAccepted(logger, message.OrderNumber, messageId);
        }
    }

    /// <summary>El POST a Zenvia, compartido por los dos documentos. Devuelve el cuerpo para leer el id
    /// del mensaje; un no-2xx sale como <paramref name="failureCode"/>.</summary>
    private async Task<string> PostAsync(
        ZenviaWhatsAppChannel channel, object payload, string failureCode, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{settings.BaseUrl.TrimEnd('/')}/v2/channels/whatsapp/messages")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-API-TOKEN", channel.ApiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new QuotationsDomainException(
                failureCode,
                $"Zenvia responded {(int)response.StatusCode}: {body}");
        }

        return body;
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

    // Zenvia manda el PDF por su copia pública: sin ella el handler no llamó a este sender como debía. Es un error de
    // programación, no del usuario, así que no es un código de dominio.
    private static string RequireDocumentUrl(string? documentUrl) =>
        documentUrl ?? throw new InvalidOperationException(
            "A Zenvia message needs the public document URL; the handler published none.");
}
