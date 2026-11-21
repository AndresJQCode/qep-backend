using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Domain;

namespace Modules.Coordinadora.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-21 §3: el cliente de Coordinadora. Dos servidores, dos protocolos: el AGS por JSON-RPC 2.0 (<c>Cotizador.cotizar</c>, con
/// <c>nit</c>, <c>apikey</c> y <c>clave</c> dentro de <c>p</c>) y el de guías por SOAP 1.2 (<c>Guias_*</c>, con <c>id_cliente</c>, <c>usuario</c> y
/// <c>clave</c> = SHA-256 hex de la contraseña dentro de <c>p</c>). Timeout de 30 s. <b>Sin reintentos propios y sin reenvío</b>: no hay clave
/// de idempotencia. 5xx, timeout, red o una respuesta ilegible son <c>Transient</c>; un <c>Fault</c> o un <c>error</c> de JSON-RPC, <c>Rejected</c>.
/// El cliente no tiene loggers (<c>RemoveAllLoggers</c>): ni las claves ni el destinatario salen a un log, y ningún mensaje de esta clase
/// interpola el cuerpo.
/// </summary>
internal sealed class CoordinadoraApiClient(HttpClient httpClient, IOptions<CoordinadoraOptions> options) : ICoordinadoraApi
{
    public const string HttpClientName = "coordinadora";

    public const string QuoteMethod = "Cotizador.cotizar";
    public const string CreateGuideOperation = "Guias_generarGuia";
    public const string PrintLabelsOperation = "Guias_imprimirRotulos";
    public const string TrackOperation = "Guias_rastreoSimple";
    public const string CancelOperation = "Guias_anularGuia";

    /// <summary>Fragmentos de nombre de elemento que delatan un dato del destinatario: no entran en el rastreo aplanado (V-5).</summary>
    internal static readonly string[] RecipientFieldFragments = ["destinatario", "direccion", "telefono", "nombre", "nit", "documento", "correo", "email", "remitente"];

    /// <summary>Sin redirecciones: las claves no viajan a otro host.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>30 s por llamada y <c>User-Agent: qep-coordinadora</c>.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-coordinadora");
    }

    /// <summary>La <c>clave</c> del servidor de guías: SHA-256 de la contraseña, en hexadecimal minúsculo (cliente PHP de referencia).</summary>
    internal static string GuidesPasswordHash(string password) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    // --- AGS (JSON-RPC) ---

    public async Task<CoordinadoraQuoteOutcome> QuoteAsync(
        CoordinadoraCredentials credentials, CoordinadoraQuoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var p = JsonNode.Parse(request.Json)?.AsObject() ?? throw new ArgumentException("The quote request must be a JSON object.", nameof(request));
        p["nit"] = credentials.Nit;
        p["apikey"] = credentials.ApiKey;
        p["clave"] = credentials.ApiPassword;
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = QuoteMethod,
            ["params"] = new JsonObject { ["p"] = p },
            ["id"] = 1,
        }.ToJsonString();

        var reply = await SendAsync(options.Value.AgsUri(), body, "application/json", null, cancellationToken);
        if (reply.TransientReason is { } transient)
        {
            return new CoordinadoraQuoteOutcome.Transient(transient);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reply.Body);
        }
        catch (JsonException)
        {
            return reply.Status is >= 200 and < 300
                ? new CoordinadoraQuoteOutcome.Transient("invalid_response")
                : new CoordinadoraQuoteOutcome.Rejected($"http_{reply.Status}", string.Empty);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                // Sin message no se vuelca el objeto: podría ecoar los parámetros recibidos, apikey y clave incluidas.
                return new CoordinadoraQuoteOutcome.Rejected(JsonText(error, "code") ?? "error", JsonText(error, "message") ?? "error");
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result) && result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                // Un result que el AGS usa para avisar un error (V-4): {"error": "...", ...} o un texto pelado.
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var resultError)
                    && resultError.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined))
                {
                    return new CoordinadoraQuoteOutcome.Rejected(JsonText(resultError, "code") ?? "error", JsonText(resultError, "message") ?? "error");
                }

                return new CoordinadoraQuoteOutcome.Quoted(result.GetRawText(), FreightTotalOf(result));
            }

            return reply.Status is >= 200 and < 300
                ? new CoordinadoraQuoteOutcome.Transient("invalid_response")
                : new CoordinadoraQuoteOutcome.Rejected($"http_{reply.Status}", string.Empty);
        }
    }

    /// <summary>El flete total del <c>result</c>: <c>flete_total</c> o <c>total</c>, en el objeto o en el primer elemento de un arreglo (V-4).</summary>
    internal static decimal? FreightTotalOf(JsonElement result)
    {
        var scope = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().FirstOrDefault() : result;
        if (scope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "flete_total", "total", "valor_flete", "flete" })
        {
            if (scope.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String && CoordinadoraSoap.Number(value.GetString()) is { } parsed)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    // --- Guías (SOAP 1.2) ---

    public async Task<CoordinadoraGuideOutcome> CreateGuideAsync(
        CoordinadoraCredentials credentials, CoordinadoraGuideRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await SoapAsync(credentials, CreateGuideOperation, request.Json, cancellationToken);
        if (reply.TransientReason is { } transient)
        {
            return new CoordinadoraGuideOutcome.Transient(transient);
        }

        var parsed = CoordinadoraSoap.Parse(reply.Body);
        if (parsed is null)
        {
            // Sin XML no se sabe qué pasó: con 2xx es «no sé si se creó»; con 4xx, un rechazo del servidor sin cuerpo útil.
            return reply.Status is >= 200 and < 300
                ? new CoordinadoraGuideOutcome.Transient("invalid_response")
                : new CoordinadoraGuideOutcome.Rejected($"http_{reply.Status}", string.Empty);
        }

        if (parsed.Fault is { } fault)
        {
            // Sólo un Fault del lado del cliente (Sender/Client: datos inválidos, credenciales) dice que Coordinadora NO creó la guía. Un
            // Receiver/Server (o uno sin código conocido) puede haber fallado después de crearla; sin clave de idempotencia, liberar el pedido
            // generaría una segunda guía: queda Unknown y la resuelve una persona.
            return CoordinadoraSoap.IsClientFault(fault.Code)
                ? new CoordinadoraGuideOutcome.Rejected(fault.Code, fault.Message)
                : new CoordinadoraGuideOutcome.Transient($"fault_{CoordinadoraSoap.FaultKind(fault.Code)}");
        }

        var guideNumber = parsed.Text("codigo_remision");
        if (!string.IsNullOrWhiteSpace(guideNumber))
        {
            return new CoordinadoraGuideOutcome.Created(guideNumber, parsed.Pdf());
        }

        // Un cuerpo sin número y sin Fault: si trae un error con nombre, es un rechazo; si no, no se sabe qué quedó en Coordinadora.
        var error = parsed.Text("error", "mensaje", "message");
        return error is not null
            ? new CoordinadoraGuideOutcome.Rejected("rejected", error)
            : new CoordinadoraGuideOutcome.Transient("invalid_response");
    }

    public async Task<byte[]> PrintLabelAsync(CoordinadoraCredentials credentials, string guideNumber, CancellationToken cancellationToken)
    {
        var parsed = await ReadAsync(credentials, PrintLabelsOperation, GuideParameters(guideNumber), cancellationToken);
        return parsed.Pdf()
            ?? throw new ServiceUnavailableException(CoordinadoraErrorCodes.Unavailable, "Coordinadora answered the label without a PDF.");
    }

    public async Task<CoordinadoraTracking> TrackAsync(CoordinadoraCredentials credentials, string guideNumber, CancellationToken cancellationToken)
    {
        var parsed = await ReadAsync(credentials, TrackOperation, GuideParameters(guideNumber), cancellationToken);
        // V-5: los nombres probables primero; si no vienen, el cuerpo aplanado para que la pantalla muestre algo y el campo se descubra.
        var status = parsed.Text("estado", "estado_guia", "estado_actual", "descripcion_estado");
        var detail = parsed.Text("descripcion", "detalle", "novedad", "observacion", "observaciones");
        // El cuerpo aplanado deja fuera lo que nombre al destinatario (nombre, dirección, teléfono, documento): tracking_detail sale en el GET.
        return status is null && detail is null
            ? new CoordinadoraTracking(null, CoordinadoraText.Cap(parsed.BodyText(RecipientFieldFragments), CoordinadoraShipment.TrackingDetailMaxLength))
            : new CoordinadoraTracking(status, detail);
    }

    public async Task<CoordinadoraCancelOutcome> CancelGuideAsync(
        CoordinadoraCredentials credentials, string guideNumber, CancellationToken cancellationToken)
    {
        var reply = await SoapAsync(credentials, CancelOperation, GuideParameters(guideNumber), cancellationToken);
        if (reply.TransientReason is { } transient)
        {
            return new CoordinadoraCancelOutcome.Transient(transient);
        }

        var parsed = CoordinadoraSoap.Parse(reply.Body);
        if (parsed is null)
        {
            return reply.Status is >= 200 and < 300
                ? new CoordinadoraCancelOutcome.Transient("invalid_response")
                : new CoordinadoraCancelOutcome.Rejected($"http_{reply.Status}", string.Empty);
        }

        if (parsed.Fault is { } fault)
        {
            return new CoordinadoraCancelOutcome.Rejected(fault.Code, fault.Message);
        }

        var error = parsed.Text("error");
        return error is not null ? new CoordinadoraCancelOutcome.Rejected("rejected", error) : new CoordinadoraCancelOutcome.Cancelled();
    }

    // Las lecturas lanzan: 503 sin respuesta o con una ilegible, 422 carrier_rejected con un Fault.
    private async Task<CoordinadoraSoap.Reply> ReadAsync(
        CoordinadoraCredentials credentials, string operation, string parametersJson, CancellationToken cancellationToken)
    {
        var reply = await SoapAsync(credentials, operation, parametersJson, cancellationToken);
        if (reply.TransientReason is { } transient)
        {
            throw new ServiceUnavailableException(CoordinadoraErrorCodes.Unavailable, $"Coordinadora did not answer ({transient}).");
        }

        var parsed = CoordinadoraSoap.Parse(reply.Body)
            ?? throw new ServiceUnavailableException(CoordinadoraErrorCodes.Unavailable, $"Coordinadora answered without XML (HTTP {reply.Status}).");
        if (parsed.Fault is { } fault)
        {
            throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.CarrierRejected, $"Coordinadora rejected {operation} ({fault.Code}): {fault.Message}");
        }

        return parsed;
    }

    private static string GuideParameters(string guideNumber) =>
        new JsonObject { ["codigo_remision"] = guideNumber.Trim() }.ToJsonString();

    private Task<Reply> SoapAsync(CoordinadoraCredentials credentials, string operation, string parametersJson, CancellationToken cancellationToken)
    {
        var ns = options.Value.EffectiveGuidesNamespace();
        var envelope = CoordinadoraSoap.Build(
            ns,
            operation,
            parametersJson,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id_cliente"] = credentials.ClientId,
                ["usuario"] = credentials.GuidesUser,
                ["clave"] = GuidesPasswordHash(credentials.GuidesPassword),
            });
        return SendAsync(options.Value.GuidesUri(), envelope, CoordinadoraSoap.ContentType, $"{ns}#{operation}", cancellationToken);
    }

    // --- Transporte ---

    private sealed record Reply(int Status, string Body, string? TransientReason);

    private async Task<Reply> SendAsync(Uri uri, string body, string mediaType, string? soapAction, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
            if (soapAction is not null)
            {
                // SOAP 1.2 lleva la acción como parámetro del Content-Type.
                request.Content.Headers.ContentType!.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("action", $"\"{soapAction}\""));
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            // Un 5xx con Fault es un rechazo con cuerpo (el servidor PHP contesta los Fault con 500): el llamador lo lee antes de decidir.
            var transient = status >= 500 && CoordinadoraSoap.Parse(text)?.Fault is null && !LooksLikeJsonRpcError(text)
                ? $"http_{status}"
                : status is >= 300 and < 400 ? $"http_{status}" : null;
            return new Reply(status, text, transient);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Reply(0, string.Empty, "timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new Reply(0, string.Empty, "network");
        }
    }

    private static bool LooksLikeJsonRpcError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? JsonText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
