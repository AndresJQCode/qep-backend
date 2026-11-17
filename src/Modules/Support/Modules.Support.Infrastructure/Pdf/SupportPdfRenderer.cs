using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Support.Application;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Pdf;

/// <summary>
/// Fase 4b (H12): genera el PDF de la respuesta formal contra <c>qcode-pdf</c> (<c>POST /pdf</c>, Typst), calco de
/// <c>QCodePdfRenderer</c> de Quotations con su propia sección y clave (<see cref="SupportPdfOptions"/>). El servicio es genérico: la
/// plantilla viaja entera en cada request, los datos van aparte en <c>data</c> (que la plantilla lee con <c>json(sys.inputs.data)</c>) y
/// el logo como asset.
/// </summary>
/// <remarks>
/// Un fallo (respuesta no 2xx, red caída, tiempo agotado) es <c>support.reply.document_render_failed</c> (422). Ni el log ni la excepción
/// llevan el cuerpo de la respuesta: el error del compilador de Typst puede repetir textos de la carta, y la carta trae datos del
/// solicitante. Sólo el status.
/// </remarks>
internal sealed partial class SupportPdfRenderer(
    HttpClient httpClient, IOptions<SupportPdfOptions> options, ILogger<SupportPdfRenderer> logger)
    : ISupportFormalReplyRenderer
{
    // camelCase a propósito: es el contrato con la plantilla, que lee `data.ticketNumber`. SupportFormalReplyTemplateTests lo amarra.
    private static readonly JsonSerializerOptions DataFormat = new(JsonSerializerDefaults.Web);

    /// <summary>La plantilla embebida; la lee también <c>SupportFormalReplyTemplateTests</c>.</summary>
    internal static readonly string Template = ReadTemplate();

    private readonly SupportPdfOptions settings = options.Value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "qcode-pdf rejected the formal reply document with status {StatusCode}.")]
    private static partial void LogRejected(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "qcode-pdf could not be reached to render the formal reply document.")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);

    public async Task<byte[]> RenderAsync(SupportFormalReplyDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Dictionary y no un anónimo: `assets` va sólo con logo, y el request de un tenant sin logo no cambia de forma
        // (contrato de qcode-pdf: { source, data, filename, assets: { "<nombre>": "<base64>" } }).
        var payload = new Dictionary<string, object?>
        {
            ["source"] = Template,
            ["data"] = JsonSerializer.SerializeToElement(document, DataFormat),
            ["filename"] = SupportReplyDocument.FileNameFor(document.TicketNumber),
        };
        if (document.Letterhead.Logo is { } logo)
        {
            payload["assets"] = new Dictionary<string, string> { [logo.FileName] = Convert.ToBase64String(logo.Content) };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.Trim().TrimEnd('/')}/pdf")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, exception);
            throw new SupportDomainException(
                SupportErrorCodes.ReplyDocumentRenderFailed, "qcode-pdf could not be reached to render the formal reply.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                LogRejected(logger, status);
                throw new SupportDomainException(SupportErrorCodes.ReplyDocumentRenderFailed, $"qcode-pdf responded {status}.");
            }

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length == 0)
            {
                LogRejected(logger, (int)response.StatusCode);
                throw new SupportDomainException(SupportErrorCodes.ReplyDocumentRenderFailed, "qcode-pdf returned an empty document.");
            }

            return content;
        }
    }

    // La plantilla viaja dentro del ensamblado: es parte del contrato con el servicio, y un archivo suelto se pierde al publicar.
    private static string ReadTemplate()
    {
        var assembly = typeof(SupportPdfRenderer).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            resource => resource.EndsWith("formal-reply.typ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Typst template 'formal-reply.typ' is not embedded in the assembly.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
