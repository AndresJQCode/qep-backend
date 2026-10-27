using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Domain.Text;

namespace Modules.Automations.Infrastructure.Http;

/// <summary>
/// Spec 2026-10-21 D-H4..D-H8: el bloque HTTP del chatbot. Resuelve la conexión <c>external-api</c> al momento de llamar (D-H5), manda
/// <c>{BaseUrl}{Path}?{Query}</c> con <c>{AuthHeader}: {ApiKey}</c>, espera a lo sumo <c>timeoutSeconds</c>, lee hasta 1 MB y guarda de la
/// respuesta sólo lo que dice el mapeo (D-H7). Cada llamada deja su fila en <c>http_calls</c> (D-H6), salvo cuando no hay conexión. Ni el
/// cuerpo, ni los headers, ni la clave, ni la URL llegan a un log: los mensajes llevan tenant, bloque, resultado, status y duración, y el
/// cliente nombrado no tiene loggers (<c>RemoveAllLoggers</c>, registrado en Bootstrapper con el handler anti-SSRF de Integrations).
/// </summary>
public sealed partial class HttpBlockExecutor(
    IHttpClientFactory httpClientFactory,
    IAutomationExternalApis externalApis,
    IExternalApiUrlPolicy urlPolicy,
    IAutomationMedia media,
    IHttpCallLog callLog,
    IClock clock,
    ILogger<HttpBlockExecutor> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IHttpBlockExecutor
{
    /// <summary>D-H5.</summary>
    public const string HttpClientName = "automations.external-api";

    /// <summary>D-H7: el tope de la respuesta.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// Desvío de D-H8 (plan 2, tarea 11): el spec dice 60 s de espera por el adjunto, pero la unidad entera corre dentro de un lease de 30 s y
    /// esperar 60 haría que otra réplica reclamara la misma fila a mitad. Se espera 20 s (4 reintentos cada 5 s): <c>MediaCopyWorker</c> corre
    /// cada 5 s y copia una imagen en menos de un segundo.
    /// </summary>
    internal const int MediaWaitRetries = 4;

    internal static readonly TimeSpan MediaWaitInterval = TimeSpan.FromSeconds(5);

    private const int MinTimeoutSeconds = 1;

    private const int MaxTimeoutSeconds = 10;

    private const string DefaultFileName = "file";

    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    [LoggerMessage(Level = LogLevel.Information, Message = "Chatbot HTTP block {NodeId} of tenant {TenantId} answered {Status}: {Outcome} in {DurationMs} ms")]
    private static partial void LogCalled(ILogger logger, Guid tenantId, string nodeId, string outcome, int? status, int durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chatbot HTTP block {NodeId} of tenant {TenantId} failed: {Outcome} (status {Status}) in {DurationMs} ms")]
    private static partial void LogFailed(ILogger logger, Guid tenantId, string nodeId, string outcome, int? status, int durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chatbot HTTP block {NodeId} of tenant {TenantId} has no usable connection: {Outcome} ({ErrorCode})")]
    private static partial void LogUnavailable(ILogger logger, Guid tenantId, string nodeId, string outcome, string errorCode);

    /// <summary>Spec 2026-10-24 §3.4.</summary>
    internal const string NotChosen = "not_chosen";

    /// <summary>Spec 2026-10-24 §3.4 (P2-R30).</summary>
    internal const string AuthHeaderReserved = "auth_header_reserved";

    // Sin la excepción: su mensaje podría traer lo que la base devolvió sobre la fila.
    [LoggerMessage(Level = LogLevel.Error, Message = "Chatbot HTTP block {NodeId} of tenant {TenantId} could not record its call ({Outcome}): {ErrorType}")]
    private static partial void LogNotRecorded(ILogger logger, Guid tenantId, string nodeId, string outcome, string errorType);

    /// <summary>D-H5: <c>User-Agent: qep-automations</c>. El tope real es el <c>timeoutSeconds</c> del bloque (1..10); éste sólo ataja una
    /// llamada a la que nadie le puso uno.</summary>
    public static void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = TimeSpan.FromSeconds(MaxTimeoutSeconds + 5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-automations");
    }

    public async Task<HttpCallResult> ExecuteAsync(HttpCallContext context, HttpNode node, RunVariables variables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(variables);

        // Sin conexión no hay fila que registrar (http_calls.connection_id no admite null). Un header reservado (P2-R30/P2-R31) es una conexión
        // guardada antes de la regla: no se usa, igual que una pausada. Spec 2026-10-24 D-T5: el motivo va en ErrorCode.
        if (node.ConnectionId is not { } connectionId)
        {
            return Unavailable(context, NotChosen, null);
        }

        var resolution = await externalApis.ResolveAsync(context.TenantId, connectionId, cancellationToken);
        if (resolution.Credentials is not { } credentials)
        {
            return Unavailable(context, resolution.UnavailableReason ?? HttpCallOutcomes.ConnectionUnavailable, resolution.ConnectionName);
        }

        if (urlPolicy.IsReservedHeader(credentials.AuthHeader))
        {
            return Unavailable(context, AuthHeaderReserved, resolution.ConnectionName);
        }

        // D-H8 con el desvío de MediaWaitRetries: un solo presupuesto de espera por bloque, lo usen la url de un adjunto o el archivo del
        // multipart, así el bloque no espera más de 20 s aunque use dos adjuntos.
        var mediaWait = new MediaWait();
        var (effective, notReady) = await WithAttachmentUrlsAsync(context.TenantId, node, variables, mediaWait, cancellationToken);
        var request = HttpBlockRequest.Build(node, effective);
        var call = new Call(context, connectionId, resolution.ConnectionName, request.Method, request.Path, clock.UtcNow);
        if (notReady is not null)
        {
            return await FinishAsync(call, notReady, cancellationToken);
        }

        if (urlPolicy.Reject(credentials.BaseUrl) is { } rejected)
        {
            return await FinishAsync(call, Failure(HttpCallOutcomes.UrlRejected, null, rejected), cancellationToken);
        }

        if (request.PathRejection is { } pathRejection)
        {
            return await FinishAsync(call, Failure(HttpCallOutcomes.UrlRejected, null, pathRejection), cancellationToken);
        }

        if (request.Body is HttpBlockBody.Json json && !json.IsWellFormed())
        {
            return await FinishAsync(call, Failure(HttpCallOutcomes.BodyNotJson, null, HttpCallOutcomes.BodyNotJson), cancellationToken);
        }

        // Una URL que no se arma (un valor enorme pasa el largo máximo de System.Uri) no puede lanzar: deshacer la unidad la reintentaría igual.
        if (UrlOf(credentials.BaseUrl, request) is not { } url)
        {
            return await FinishAsync(call, Failure(HttpCallOutcomes.UrlRejected, null, "url_invalid"), cancellationToken);
        }

        HttpContent? content;
        switch (request.Body)
        {
            case HttpBlockBody.Json body:
                content = new StringContent(body.Content, Encoding.UTF8, "application/json");
                break;
            case HttpBlockBody.Multipart form:
                var built = await FormAsync(context.TenantId, form, effective, mediaWait, cancellationToken);
                if (built.Failure is { } notBuilt)
                {
                    return await FinishAsync(call, notBuilt, cancellationToken);
                }

                content = built.Content;
                break;
            default:
                content = null;
                break;
        }

        // P2-R46: la duración de http_calls es la de la llamada; la espera del adjunto (hasta 20 s) no cuenta.
        call = call with { SentAt = Stopwatch.GetTimestamp() };
        var sent = await SendAsync(node, credentials, request.Method, url, content, cancellationToken);
        // P2-R33a: la llamada ya salió. Un apagado justo ahora no puede perder la fila: el reintento volvería a llamar sin rastro de la primera.
        return await FinishAsync(call, sent.Result, CancellationToken.None, sent.Body?.Length);
    }

    private async Task<Sent> SendAsync(
        HttpNode node, ExternalApiCredentials credentials, string method, Uri url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
        message.Headers.TryAddWithoutValidation(credentials.AuthHeader, credentials.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(node.TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds)));
        int? answered = null;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            answered = status;
            // Un status que no cumple no necesita el cuerpo: de él no se guarda nada.
            if (!IsOk(node.OkWhen, status))
            {
                return new Sent(Failure(HttpCallOutcomes.ErrorStatus, status, $"http_{status}"));
            }

            if (await ReadCappedAsync(response.Content, timeout.Token) is not { } body)
            {
                return new Sent(Failure(HttpCallOutcomes.ResponseTooLarge, status, HttpCallOutcomes.ResponseTooLarge));
            }

            return new Sent(Map(node.Mapping, status, body), body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El timeout del bloque (o el del cliente), no una cancelación de quien llama: ésa se propaga.
            return new Sent(Failure(HttpCallOutcomes.Timeout, answered, HttpCallOutcomes.Timeout));
        }
        catch (HttpRequestException exception)
        {
            // D-H2: ConnectCallback rechazó lo que resolvió el host. Es la URL, no la red.
            return new Sent(
                urlPolicy.RejectionIn(exception) is { } reason
                    ? Failure(HttpCallOutcomes.UrlRejected, null, reason)
                    : Failure(HttpCallOutcomes.Network, answered, HttpCallOutcomes.Network));
        }
        catch (IOException)
        {
            // Revisión de la tarea 11: con ResponseHeadersRead, un corte a mitad del cuerpo llega como HttpIOException (IOException, no
            // HttpRequestException). La API ya recibió el request: lanzar desharía la unidad y el reintento la volvería a llamar.
            return new Sent(Failure(HttpCallOutcomes.Network, answered, HttpCallOutcomes.Network));
        }
    }

    // D-H7: sólo el mapeo guarda algo; sin mapeo el cuerpo ni se lee como JSON. El paso del mapeo es el mismo del simulador (HttpResponseMapping).
    private static HttpCallResult Map(IReadOnlyList<HttpMapping> mapping, int status, byte[] body)
    {
        if (mapping.Count == 0)
        {
            return new HttpCallResult(true, status, RunVariables.Empty(), HttpCallOutcomes.Ok, null);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Failure(HttpCallOutcomes.MappingFailed, status, HttpCallOutcomes.BodyNotJson);
        }

        return new HttpCallResult(true, status, HttpResponseMapping.Apply(mapping, root), HttpCallOutcomes.Ok, null);
    }

    private static bool IsOk(IReadOnlyList<int>? okWhen, int status) => okWhen is null ? status is >= 200 and < 300 : okWhen.Contains(status);

    // D-H7: ni un Content-Length de más ni un cuerpo sin él que pase el tope; nunca se lee más de MaxResponseBytes + un bloque.
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // La URL base sin su «/» final más la ruta (que empieza con «/»): /v1 + /customers es /v1/customers, no /customers como resolvería
    // new Uri(base, relativa). Igual que el probador de la conexión (ExternalApiConnectionTester).
    private static Uri? UrlOf(Uri baseUrl, HttpBlockRequest request)
    {
        var url = new StringBuilder(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        if (!request.Path.StartsWith('/'))
        {
            url.Append('/');
        }

        url.Append(request.Path);
        var separator = '?';
        foreach (var (key, value) in request.Query)
        {
            url.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return Uri.TryCreate(url.ToString(), UriKind.Absolute, out var built) ? built : null;
    }

    /// <summary>
    /// Ruling P2-R43: la ruta, la query o el cuerpo usan <c>{x.url}</c> de un adjunto que todavía no está en R2 (<c>stored: false</c>). Se
    /// espera como al archivo del multipart y, cuando llega, se firma la url con la llave que da Mensajería sobre una copia de las variables
    /// (la corrida guarda la suya cuando la refresca <c>RunEffects</c>). Si no llega, el bloque sale por <c>media_not_ready</c> sin llamar.
    /// </summary>
    private async Task<(RunVariables Variables, HttpCallResult? NotReady)> WithAttachmentUrlsAsync(
        Guid tenantId, HttpNode node, RunVariables variables, MediaWait mediaWait, CancellationToken cancellationToken)
    {
        RunVariables? effective = null;
        foreach (var name in AttachmentUrlRoots(node).Distinct(StringComparer.Ordinal))
        {
            if (AttachmentUrls.PendingMessageId(variables, name) is not { } messageId)
            {
                continue;
            }

            if (await StoredAsync(tenantId, messageId, mediaWait, cancellationToken) is not { } stored)
            {
                return (variables, Failure(HttpCallOutcomes.MediaNotReady, null, HttpCallOutcomes.MediaNotReady));
            }

            effective ??= variables.Clone();
            AttachmentUrls.MarkStored(effective, name, await media.CreateDownloadUrlAsync(stored.Key, AttachmentUrls.Lifetime, cancellationToken));
        }

        return (effective ?? variables, null);
    }

    // Las variables raíz de cada {x.url} de la ruta, la query y el cuerpo (plantilla JSON o campos del multipart): el bloque Adjunto guarda
    // en una raíz, así que una url más adentro (a.b.url) no es de un adjunto.
    private static IEnumerable<string> AttachmentUrlRoots(HttpNode node)
    {
        IEnumerable<string> templates = [node.Path, .. node.Query.Select(pair => pair.Value)];
        if (node.Body is { } body)
        {
            templates = templates.Concat(body.Template is null ? [] : [body.Template]).Concat((body.Fields ?? []).Select(pair => pair.Value));
        }

        return templates
            .SelectMany(TemplateText.ReferencesIn)
            .Where(reference => reference.EndsWith(".url", StringComparison.Ordinal))
            .Select(reference => reference[..^".url".Length])
            .Where(root => !root.Contains('.', StringComparison.Ordinal) && !root.Contains('[', StringComparison.Ordinal));
    }

    private async Task<(HttpContent? Content, HttpCallResult? Failure)> FormAsync(
        Guid tenantId, HttpBlockBody.Multipart form, RunVariables variables, MediaWait mediaWait, CancellationToken cancellationToken)
    {
        StreamContent? file = null;
        string? fileName = null;
        if (form is { FileField: not null, FileVariable: { } fileVariable })
        {
            // La variable la escribió el bloque Adjunto (CustomerReply.Receive): sólo su messageId cuenta; la llave de R2 sale de Mensajería.
            if (!Guid.TryParse(variables.TextOf($"{fileVariable}.messageId"), out var messageId))
            {
                return (null, Failure(HttpCallOutcomes.MediaNotReady, null, "attachment_missing"));
            }

            if (await StoredAsync(tenantId, messageId, mediaWait, cancellationToken) is not { } stored
                || await media.OpenReadAsync(stored.Key, cancellationToken) is not { } opened)
            {
                return (null, Failure(HttpCallOutcomes.MediaNotReady, null, HttpCallOutcomes.MediaNotReady));
            }

            file = new StreamContent(opened.Content);
            // P2-R46: el stream de R2 es una respuesta de red que no se puede buscar, así que StreamContent no calcula el largo y la parte
            // viajaría sin Content-Length; el largo lo da el almacén.
            file.Headers.ContentLength = opened.Length;
            file.Headers.ContentType = MediaTypeHeaderValue.TryParse(stored.MimeType, out var type) ? type : null;
            fileName = SafeFileName(stored.FileName);
        }

        var content = new MultipartFormDataContent();
        try
        {
            foreach (var (key, value) in form.Fields)
            {
                // Sin Content-Type, como manda un navegador un campo de texto: algunas APIs leen un campo con tipo como si fuera un archivo.
                var field = new StringContent(value);
                field.Headers.ContentType = null;
                content.Add(field, key);
            }

            if (file is not null)
            {
                content.Add(file, form.FileField!, fileName!);
                file = null;
            }

            return (content, null);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Un nombre de campo que el header Content-Disposition no admite (comillas o saltos de línea en lo que escribió el admin): el
            // cuerpo no se puede armar. Se cierra el archivo de R2 y el bloque sale por «error» con su fila, sin lanzar.
            content.Dispose();
            file?.Dispose();
            return (null, Failure(HttpCallOutcomes.BodyNotJson, null, "form_invalid"));
        }
    }

    // Revisión de la tarea 11: el nombre lo puso el cliente en WhatsApp (message_media.file_name sólo se recorta). Una comilla, una barra
    // invertida o un control (CR/LF) rompen el header Content-Disposition o le inyectan otro: se quitan, y si no queda nada va «file».
    private static string SafeFileName(string? name)
    {
        var safe = new string([.. (name ?? string.Empty).Where(character => character is not ('"' or '\\') && !char.IsControl(character))]).Trim();
        return safe.Length == 0 ? DefaultFileName : safe;
    }

    // D-H8 con el desvío de MediaWaitRetries: el adjunto puede llegar antes de que MediaCopyWorker lo copie a R2. Los reintentos salen del
    // presupuesto del bloque (MediaWait), compartido entre la url de un adjunto y el archivo del multipart.
    private async Task<StoredAttachment?> StoredAsync(Guid tenantId, Guid messageId, MediaWait mediaWait, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await media.TryGetStoredAsync(tenantId, messageId, cancellationToken) is { } stored)
            {
                return stored;
            }

            if (mediaWait.Remaining == 0)
            {
                return null;
            }

            mediaWait.Remaining--;
            await wait(MediaWaitInterval, cancellationToken);
        }
    }

    private static HttpCallResult Failure(string outcome, int? status, string errorCode) =>
        new(false, status, RunVariables.Empty(), outcome, errorCode);

    // Sin fila: http_calls.connection_id no admite null y una conexión que no se usó no es una llamada. El log no lleva URL ni clave.
    private HttpCallResult Unavailable(HttpCallContext context, string reason, string? connectionName)
    {
        LogUnavailable(logger, context.TenantId, context.NodeId, HttpCallOutcomes.ConnectionUnavailable, reason);
        return new HttpCallResult(false, null, RunVariables.Empty(), HttpCallOutcomes.ConnectionUnavailable, reason, connectionName);
    }

    private async Task<HttpCallResult> FinishAsync(
        Call call, HttpCallResult result, CancellationToken cancellationToken, int? responseBytes = null)
    {
        // Sin llamada (url rechazada, adjunto que no llegó, cuerpo que no se armó) no hay duración que medir.
        var durationMs = call.SentAt is { } sentAt ? (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds) : 0;
        if (result.Ok)
        {
            LogCalled(logger, call.Context.TenantId, call.Context.NodeId, result.Outcome, result.Status, durationMs);
        }
        else
        {
            LogFailed(logger, call.Context.TenantId, call.Context.NodeId, result.Outcome, result.Status, durationMs);
        }

        try
        {
            await callLog.RecordAsync(
                new HttpCallEntry(
                    call.Context.TenantId, call.Context.RunId, call.Context.SimulationId, call.Context.NodeId, call.ConnectionId, call.Method,
                    call.Path, result.Status, durationMs, result.Outcome, result.ErrorCode, call.CalledAt, call.Context.StepNumber, responseBytes),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // La llamada ya salió y no se repite: si la fila falla, el bloque sigue con su resultado. Deshacer la unidad por el registro haría
            // que el reintento volviera a llamar a la API (un POST que crea una orden, dos veces).
            LogNotRecorded(logger, call.Context.TenantId, call.Context.NodeId, result.Outcome, exception.GetType().Name);
        }

        return result with { ConnectionName = call.ConnectionName };
    }

    /// <summary><see cref="SentAt"/>: el <c>Stopwatch</c> justo antes de mandar; <c>null</c> si el bloque no llegó a llamar.
    /// <see cref="ConnectionName"/> viaja con el resultado (spec 2026-10-24 §3.2), no a la fila.</summary>
    private sealed record Call(
        HttpCallContext Context, Guid ConnectionId, string? ConnectionName, string Method, string Path, DateTimeOffset CalledAt, long? SentAt = null);

    /// <summary>Lo que dejó la llamada: el resultado del bloque y el cuerpo leído (D-E3: de él sólo se guarda el largo en <c>response_bytes</c>);
    /// <see cref="Body"/> es <c>null</c> si no se leyó ninguno.</summary>
    private sealed record Sent(HttpCallResult Result, byte[]? Body = null);

    /// <summary>Los reintentos de espera del adjunto que le quedan al bloque (D-H8 con el desvío: 4 × 5 s en total).</summary>
    private sealed class MediaWait
    {
        public int Remaining { get; set; } = MediaWaitRetries;
    }
}
