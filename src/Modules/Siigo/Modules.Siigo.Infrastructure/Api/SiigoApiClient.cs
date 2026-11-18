using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-18 §3 y §5.4: el cliente HTTP de Siigo. <c>Partner-Id</c> en toda llamada; token por conexión en
/// <see cref="SiigoTokenCache"/>; <b>un 401 invalida el token, reautentica una vez y repite la misma petición</b> (con la misma
/// <c>Idempotency-Key</c>), y un segundo 401 es <c>credentials_rejected</c>. Timeout de 30 s en el <c>HttpClient</c> (cubre también la
/// lectura del cuerpo, que se hace con el contenido bufereado). <b>Sin reintentos propios</b>: 429, 5xx, timeout y red son
/// <c>Transient</c> y reintenta quien llama con la misma fila. Las listas se cachean 5 minutos por conexión (<c>siigo:{connectionId}:{lista}</c>);
/// la creación de facturas y los clientes nunca. El cliente no tiene loggers (<c>RemoveAllLoggers</c>): ni la clave de acceso, ni el token,
/// ni el cuerpo de una factura, ni la identificación de un cliente en una URL salen a un log.
/// </summary>
internal sealed class SiigoApiClient(
    HttpClient httpClient,
    IOptions<SiigoOptions> options,
    SiigoTokenCache tokens,
    IMemoryCache cache) : ISiigoApi
{
    public const string HttpClientName = "siigo";

    /// <summary>Spec 2026-11-18 §3: alfanumérica, ASCII, sin espacios ni especiales, hasta 30 caracteres.</summary>
    public const int IdempotencyKeyMaxLength = 30;

    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromMinutes(5);

    // Si el token viene sin expires_in, lo que Siigo documenta: un día.
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(24);

    /// <summary>Sin redirecciones: la clave y el token no viajan a otro host.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary>30 s por llamada y <c>User-Agent: qep-siigo</c>.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-siigo");
    }

    // --- Listas (con caché) ---

    public Task<IReadOnlyList<SiigoOption>> ListDocumentTypesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "document-types", () => ReadOptionsAsync(credentials, "v1/document-types?type=FV", cancellationToken));

    public Task<IReadOnlyList<SiigoOption>> ListUsersAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "users", () => ReadOptionsAsync(credentials, "v1/users", cancellationToken));

    public Task<IReadOnlyList<SiigoOption>> ListCostCentersAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "cost-centers", () => ReadOptionsAsync(credentials, "v1/cost-centers", cancellationToken));

    public Task<IReadOnlyList<SiigoOption>> ListWarehousesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "warehouses", () => ReadOptionsAsync(credentials, "v1/warehouses", cancellationToken));

    public Task<IReadOnlyList<SiigoOption>> ListPaymentTypesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "payment-types", () => ReadOptionsAsync(credentials, "v1/payment-types?document_type=FV", cancellationToken));

    public Task<IReadOnlyList<SiigoTaxOption>> ListTaxesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "taxes", async () =>
        {
            var reply = await GetAsync(credentials, "v1/taxes", cancellationToken);
            return (IReadOnlyList<SiigoTaxOption>)
            [
                .. SiigoJson.Items(reply.Body).Select(item => new SiigoTaxOption(
                    SiigoJson.Text(item, "id") ?? string.Empty,
                    SiigoJson.Text(item, "name") ?? string.Empty,
                    SiigoJson.Text(item, "type") ?? string.Empty,
                    SiigoJson.Decimal(item, "percentage") ?? 0m,
                    SiigoJson.Bool(item, "active", fallback: true))).Where(tax => tax.Id.Length > 0),
            ];
        });

    // --- Productos y clientes (sin caché) ---

    public async Task<SiigoPage<SiigoProductOption>> SearchProductsAsync(
        SiigoCredentials credentials, string? code, int page, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("v1/products?page=")
            .Append(Math.Max(page, 1).ToString(CultureInfo.InvariantCulture))
            .Append("&page_size=")
            .Append(SiigoApiErrors.ProductsPageSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(code))
        {
            query.Append("&code=").Append(Uri.EscapeDataString(code.Trim()));
        }

        var reply = await GetAsync(credentials, query.ToString(), cancellationToken);
        var items = SiigoJson.Items(reply.Body)
            .Select(item => new SiigoProductOption(
                SiigoJson.Text(item, "id") ?? string.Empty,
                SiigoJson.Text(item, "code") ?? string.Empty,
                SiigoJson.Text(item, "name") ?? string.Empty,
                SiigoJson.Bool(item, "active", fallback: true)))
            .Where(product => product.Id.Length > 0)
            .ToList();
        var pagination = SiigoJson.Pagination(reply.Body);
        return new SiigoPage<SiigoProductOption>(
            items,
            pagination.Page ?? Math.Max(page, 1),
            pagination.PageSize ?? SiigoApiErrors.ProductsPageSize,
            pagination.TotalResults ?? items.Count);
    }

    public async Task<SiigoCustomerRef?> FindCustomerAsync(
        SiigoCredentials credentials, string identification, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(
            credentials, $"v1/customers?identification={Uri.EscapeDataString(identification.Trim())}", cancellationToken);
        return SiigoJson.Items(reply.Body)
            .Where(item => SiigoJson.Bool(item, "active", fallback: true))
            .Select(item => new SiigoCustomerRef(
                SiigoJson.Text(item, "id") ?? string.Empty, SiigoJson.Text(item, "identification") ?? identification.Trim()))
            .FirstOrDefault(customer => customer.Id.Length > 0);
    }

    public async Task<SiigoCustomerRef> CreateCustomerAsync(
        SiigoCredentials credentials, SiigoCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await CallAsync(credentials, HttpMethod.Post, "v1/customers", SiigoJson.CustomerBody(request), null, cancellationToken);
        ThrowUnlessSuccess(reply);
        using var document = SiigoJson.Parse(reply.Body);
        var id = document is null ? null : SiigoJson.Text(document.RootElement, "id");
        return string.IsNullOrEmpty(id)
            ? throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, "Siigo answered the customer without an id.")
            : new SiigoCustomerRef(id, request.Identification);
    }

    // --- Facturas ---

    public async Task<SiigoInvoiceOutcome> CreateInvoiceAsync(
        SiigoCredentials credentials, SiigoInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidIdempotencyKey(idempotencyKey))
        {
            // Un error de programación, no un resultado de Siigo: nada sale. El valor no se escribe: es una clave que viaja como header.
            throw new ArgumentException(
                $"The idempotency key must be 1 to {IdempotencyKeyMaxLength} ASCII letters or digits.", nameof(idempotencyKey));
        }

        var reply = await CallAsync(credentials, HttpMethod.Post, "v1/invoices", request.Json, idempotencyKey, cancellationToken);
        if (reply.TransientReason is { } transient)
        {
            return new SiigoInvoiceOutcome.Transient(transient);
        }

        if (reply.CredentialsRejected)
        {
            return new SiigoInvoiceOutcome.Rejected([new SiigoError(SiigoApiErrors.CredentialsRejected, string.Empty)], null);
        }

        if (!reply.Success)
        {
            var errors = SiigoJson.Errors(reply.Body, reply.Status);
            var total = errors
                .Where(error => string.Equals(error.Code, SiigoApiErrors.InvalidTotalPayments, StringComparison.OrdinalIgnoreCase))
                .Select(error => SiigoApiErrors.ParseExpectedPaymentsTotal(error.Message))
                .FirstOrDefault(value => value is not null);
            return new SiigoInvoiceOutcome.Rejected(errors, total);
        }

        using var document = SiigoJson.Parse(reply.Body);
        var root = document?.RootElement;
        var id = root is { } element ? SiigoJson.Text(element, "id") : null;
        // Un 2xx sin id es «no sé si se creó»: reintentar con la misma clave es lo seguro; darla por creada sin id, no.
        return string.IsNullOrEmpty(id)
            ? new SiigoInvoiceOutcome.Transient("invalid_response")
            : new SiigoInvoiceOutcome.Created(
                id,
                SiigoJson.Text(root!.Value, "name") ?? string.Empty,
                SiigoJson.Decimal(root.Value, "total") ?? 0m,
                SiigoJson.Cufe(root.Value));
    }

    public async Task<SiigoStampOutcome> StampInvoiceAsync(
        SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(
            credentials, HttpMethod.Post, $"v1/invoices/{Uri.EscapeDataString(siigoInvoiceId)}/stamp", "{}", null, cancellationToken);
        if (reply.TransientReason is { } transient)
        {
            return new SiigoStampOutcome.Transient(transient);
        }

        if (reply.CredentialsRejected)
        {
            return new SiigoStampOutcome.Rejected([new SiigoError(SiigoApiErrors.CredentialsRejected, string.Empty)]);
        }

        if (!reply.Success)
        {
            return new SiigoStampOutcome.Rejected(SiigoJson.Errors(reply.Body, reply.Status));
        }

        using var document = SiigoJson.Parse(reply.Body);
        return new SiigoStampOutcome.Stamped(document is null ? null : SiigoJson.Cufe(document.RootElement));
    }

    public async Task<IReadOnlyList<SiigoError>> GetStampErrorsAsync(
        SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(credentials, $"v1/invoices/{Uri.EscapeDataString(siigoInvoiceId)}/stamp/errors", cancellationToken);
        // Sin errores es «la DIAN no rechazó nada», no un rechazo sin causa.
        return SiigoJson.Errors(reply.Body, reply.Status, fallbackToStatus: false);
    }

    // --- Transporte ---

    private async Task<IReadOnlyList<T>> CachedAsync<T>(SiigoCredentials credentials, string list, Func<Task<IReadOnlyList<T>>> read)
    {
        var key = $"siigo:{credentials.ConnectionId}:{list}";
        if (cache.TryGetValue(key, out IReadOnlyList<T>? cached) && cached is not null)
        {
            return cached;
        }

        var value = await read();
        cache.Set(key, value, ListCacheTtl);
        return value;
    }

    private async Task<IReadOnlyList<SiigoOption>> ReadOptionsAsync(SiigoCredentials credentials, string path, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(credentials, path, cancellationToken);
        return
        [
            .. SiigoJson.Items(reply.Body)
                .Where(item => SiigoJson.Bool(item, "active", fallback: true))
                .Select(item => new SiigoOption(SiigoJson.Text(item, "id") ?? string.Empty, SiigoJson.NameOf(item)))
                .Where(option => option.Id.Length > 0),
        ];
    }

    private async Task<Reply> GetAsync(SiigoCredentials credentials, string path, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(credentials, HttpMethod.Get, path, null, null, cancellationToken);
        ThrowUnlessSuccess(reply);
        return reply;
    }

    // Las lecturas lanzan; la creación de facturas y el sellado no (ver ISiigoApi).
    private static void ThrowUnlessSuccess(Reply reply)
    {
        if (reply.TransientReason is { } transient)
        {
            throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, $"Siigo did not answer in time or failed ({transient}).");
        }

        if (reply.CredentialsRejected)
        {
            throw new ResourceConflictException(SiigoErrorCodes.ConnectionUnavailable, "Siigo rejected the credentials of this connection.");
        }

        if (!reply.Success)
        {
            throw new UpstreamServiceException("siigo.unexpected_response", $"Siigo answered HTTP {(int)reply.Status!.Value}.");
        }
    }

    private async Task<Reply> CallAsync(
        SiigoCredentials credentials,
        HttpMethod method,
        string path,
        string? body,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.PartnerId))
        {
            // Sin Partner-Id Siigo rechaza toda llamada, y esas cuentan para el bloqueo del 80 %: no se llama.
            return Reply.Transient("partner_id_missing");
        }

        for (var attempt = 0; ; attempt++)
        {
            var token = tokens.TryGet(credentials.ConnectionId);
            if (token is null)
            {
                var authentication = await AuthenticateAsync(credentials, cancellationToken);
                if (authentication.Failure is { } failure)
                {
                    return failure;
                }

                token = authentication.Token!;
            }

            var reply = await SendAsync(method, path, body, idempotencyKey, token, cancellationToken);
            if (reply.Status != HttpStatusCode.Unauthorized)
            {
                return reply;
            }

            // 401: el token pudo vencer o revocarse. Se olvida (si sigue siendo ése), se reautentica una vez y se repite lo mismo.
            tokens.Invalidate(credentials.ConnectionId, token);
            if (attempt > 0)
            {
                return Reply.Rejected();
            }
        }
    }

    private async Task<(string? Token, Reply? Failure)> AuthenticateAsync(SiigoCredentials credentials, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { username = credentials.Username, access_key = credentials.AccessKey });
        var reply = await SendAsync(HttpMethod.Post, "auth", body, null, null, cancellationToken);
        if (reply.TransientReason is not null)
        {
            return (null, reply);
        }

        if (!reply.Success)
        {
            // 401, 403 y el resto de los 4xx de /auth son la clave o el usuario; 429 y 5xx ya salieron arriba como transitorios.
            return (null, Reply.Rejected());
        }

        using var document = SiigoJson.Parse(reply.Body);
        var token = document is null ? null : SiigoJson.Text(document.RootElement, "access_token");
        if (string.IsNullOrEmpty(token))
        {
            return (null, Reply.Transient("auth_without_token"));
        }

        var seconds = SiigoJson.Decimal(document!.RootElement, "expires_in");
        tokens.Set(credentials.ConnectionId, token, seconds is > 0 ? TimeSpan.FromSeconds((double)seconds.Value) : DefaultTokenLifetime);
        return (token, null);
    }

    private async Task<Reply> SendAsync(
        HttpMethod method, string path, string? body, string? idempotencyKey, string? token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
            request.Headers.TryAddWithoutValidation("Partner-Id", options.Value.PartnerId!.Trim());
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (idempotencyKey is not null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            // Contenido bufereado (el default): el timeout del HttpClient cubre también la lectura del cuerpo.
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return Reply.From(response.StatusCode, text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // El timeout del HttpClient, no una cancelación de quien llama.
            return Reply.Transient("timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Reply.Transient("network");
        }
    }

    private Uri BaseUri =>
        new((string.IsNullOrWhiteSpace(options.Value.BaseUrl) ? SiigoOptions.DefaultBaseUrl : options.Value.BaseUrl.Trim()).TrimEnd('/') + "/");

    private static bool IsValidIdempotencyKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= IdempotencyKeyMaxLength && key.All(char.IsAsciiLetterOrDigit);

    /// <summary>Lo que volvió de una llamada: el status y el cuerpo, o el motivo por el que no se pudo (transitorio) o las credenciales
    /// no sirvieron. El cuerpo puede traer datos del cliente: nunca se imprime.</summary>
    private sealed record Reply(HttpStatusCode? Status, string Body, string? TransientReason, bool CredentialsRejected)
    {
        public bool Success => Status is { } status && (int)status is >= 200 and < 300;

        public static Reply From(HttpStatusCode status, string body)
        {
            var code = (int)status;
            // 429 y 5xx (y un 3xx, que no se sigue) no dicen nada de la petición: se reintenta.
            return code is 429 or >= 500 or (>= 300 and < 400) or 408
                ? new Reply(status, string.Empty, $"http_{code}", false)
                : new Reply(status, body, null, false);
        }

        public static Reply Transient(string reason) => new(null, string.Empty, reason, false);

        public static Reply Rejected() => new(null, string.Empty, null, true);

        public override string ToString() => "Reply { }";
    }
}
