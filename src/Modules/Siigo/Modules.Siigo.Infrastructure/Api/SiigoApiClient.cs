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
/// <c>Idempotency-Key</c>), y un segundo 401 es <c>credentials_rejected</c>. <b>Cada llamada lleva su propio plazo</b>
/// (<see cref="SiigoApiTimeouts"/>, spec 2026-11-24 §4), que cubre el envío y la lectura del cuerpo; el <c>HttpClient</c> no corta por
/// tiempo. <b>Sin reintentos propios</b>: 429, 5xx, timeout y red son
/// <c>Transient</c> y reintenta quien llama con la misma fila. Las listas se cachean 5 minutos por conexión (<c>siigo:{connectionId}:{lista}</c>);
/// la creación de facturas y los clientes nunca. El cliente no tiene loggers (<c>RemoveAllLoggers</c>): ni la clave de acceso, ni el token,
/// ni el cuerpo de una factura, ni la identificación de un cliente en una URL salen a un log.
/// </summary>
internal sealed class SiigoApiClient(
    HttpClient httpClient,
    IOptions<SiigoOptions> options,
    SiigoTokenCache tokens,
    IMemoryCache cache,
    ISiigoCallFailureLog? failures = null,
    SiigoMetrics? metrics = null,
    SiigoApiTimeouts? timeouts = null) : ISiigoApi
{
    public const string HttpClientName = "siigo";

    /// <summary>El plazo de toda llamada que no sea crear una factura. Junto con <see cref="InvoiceCreateTimeout"/> tiene que caber en
    /// <see cref="SiigoInvoice.SendLease"/> (<c>SiigoTimeoutBudgetTests</c>).</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>El plazo de <c>POST v1/invoices</c>: Siigo recomienda 120 s o más para crear documentos. Ningún request HTTP de QEP lo
    /// espera: la factura la manda el worker de las tandas.</summary>
    internal static readonly TimeSpan InvoiceCreateTimeout = TimeSpan.FromSeconds(120);

    private readonly SiigoApiTimeouts _timeouts = timeouts ?? SiigoApiTimeouts.Production;

    /// <summary>Spec 2026-11-18 §3: alfanumérica, ASCII, sin espacios ni especiales, hasta 30 caracteres.</summary>
    public const int IdempotencyKeyMaxLength = 30;

    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromMinutes(5);

    // El selector de productos del paso 6 lee todo el catálogo: la copia dura lo que dura teclear y paginar, no más.
    private static readonly TimeSpan ProductsCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Tope de páginas, el mismo del auto-vínculo: un Siigo que siguiera devolviendo páginas llenas no cuelga la petición.</summary>
    private const int MaxProductPages = 1000;
    private const int DefaultListPageSize = 25;
    private const int MaxListPages = 100;

    // Si el token viene sin expires_in, lo que Siigo documenta: un día.
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(24);

    /// <summary>Sin redirecciones: la clave y el token no viajan a otro host.</summary>
    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    /// <summary><c>User-Agent: qep-siigo</c> y sin plazo del <c>HttpClient</c>: es uno solo para todas las llamadas, y cada una pone el
    /// suyo.</summary>
    internal static void ConfigureClient(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-siigo");
    }

    // --- Listas (con caché) ---

    public Task<IReadOnlyList<SiigoOption>> ListDocumentTypesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "document-types", () => ReadOptionsAsync(credentials, "v1/document-types?type=FV", cancellationToken));

    public Task<IReadOnlyList<SiigoUser>> ListUsersAsync(SiigoCredentials credentials, bool refresh, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "users", refresh, async () => (IReadOnlyList<SiigoUser>)
        [
            .. (await ReadAllPagesAsync(credentials, "v1/users", cancellationToken))
                .Where(item => SiigoJson.Bool(item, "active", fallback: true))
                .Select(item => new SiigoUser(SiigoJson.Text(item, "id") ?? string.Empty, SiigoJson.NameOf(item), SiigoJson.Text(item, "email")))
                .Where(user => user.Id.Length > 0),
        ]);

    public Task<IReadOnlyList<SiigoOption>> ListCostCentersAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "cost-centers", () => ReadOptionsAsync(credentials, "v1/cost-centers", cancellationToken));

    public Task<IReadOnlyList<SiigoOption>> ListWarehousesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "warehouses", () => ReadOptionsAsync(credentials, "v1/warehouses", cancellationToken));

    public Task<IReadOnlyList<SiigoOption>> ListPaymentTypesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "payment-types", () => ReadOptionsAsync(credentials, "v1/payment-types?document_type=FV", cancellationToken));

    public Task<IReadOnlyList<SiigoTaxOption>> ListTaxesAsync(SiigoCredentials credentials, CancellationToken cancellationToken) =>
        CachedAsync(credentials, "taxes", async () =>
        {
            return (IReadOnlyList<SiigoTaxOption>)
            [
                .. (await ReadAllPagesAsync(credentials, "v1/taxes", cancellationToken)).Select(item => new SiigoTaxOption(
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

        var reply = await GetAsync(credentials, SiigoCallOperation.ProductSearch, query.ToString(), null, cancellationToken);
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

    public async Task<IReadOnlyList<SiigoProductOption>> ListActiveProductsAsync(
        SiigoCredentials credentials, bool refresh, CancellationToken cancellationToken)
    {
        var key = $"siigo:{credentials.ConnectionId}:products";
        if (!refresh && cache.TryGetValue(key, out IReadOnlyList<SiigoProductOption>? cached) && cached is not null)
        {
            return cached;
        }

        var active = new List<SiigoProductOption>();
        for (var page = 1; page <= MaxProductPages; page++)
        {
            var products = await SearchProductsAsync(credentials, null, page, cancellationToken);
            active.AddRange(products.Items.Where(product => product.Active));
            if (products.Items.Count == 0 || products.Page * products.PageSize >= products.TotalResults)
            {
                break;
            }
        }

        cache.Set(key, (IReadOnlyList<SiigoProductOption>)active, ProductsCacheTtl);
        return active;
    }

    public async Task<SiigoCustomerRef?> FindCustomerAsync(
        SiigoCredentials credentials, string identification, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(
            credentials,
            SiigoCallOperation.CustomerFind,
            $"v1/customers?identification={Uri.EscapeDataString(identification.Trim())}",
            null,
            cancellationToken);
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
        var reply = await CallAsync(
            credentials, HttpMethod.Post, "v1/customers", SiigoJson.CustomerBody(request), null, _timeouts.Default, cancellationToken);
        await ReportFailureAsync(credentials, SiigoCallOperation.CustomerCreate, reply, null, null, cancellationToken);
        ThrowUnlessSuccess(reply);
        using var document = SiigoJson.Parse(reply.Body);
        var id = document is null ? null : SiigoJson.Text(document.RootElement, "id");
        if (string.IsNullOrEmpty(id))
        {
            await ReportInvalidResponseAsync(credentials, SiigoCallOperation.CustomerCreate, reply, null, cancellationToken);
            throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, "Siigo answered the customer without an id.");
        }

        return new SiigoCustomerRef(id, request.Identification);
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

        var reply = await CallAsync(
            credentials, HttpMethod.Post, "v1/invoices", request.Json, idempotencyKey, _timeouts.InvoiceCreate, cancellationToken);
        await ReportFailureAsync(credentials, SiigoCallOperation.InvoiceCreate, reply, idempotencyKey, null, cancellationToken);
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
        // Un 2xx sin id es «no sé si se creó»: reintentar con la misma clave es lo seguro; darla por creada sin id, no. Su curl y su respuesta
        // se guardan: es el caso ambiguo que más pide diagnóstico.
        if (string.IsNullOrEmpty(id))
        {
            await ReportInvalidResponseAsync(credentials, SiigoCallOperation.InvoiceCreate, reply, idempotencyKey, cancellationToken);
            return new SiigoInvoiceOutcome.Transient("invalid_response");
        }

        return new SiigoInvoiceOutcome.Created(
            id,
            SiigoJson.Text(root!.Value, "name") ?? string.Empty,
            SiigoJson.Decimal(root.Value, "total") ?? 0m,
            SiigoJson.Cufe(root.Value));
    }

    public async Task<SiigoStampOutcome> StampInvoiceAsync(
        SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(
            credentials,
            HttpMethod.Post,
            $"v1/invoices/{Uri.EscapeDataString(siigoInvoiceId)}/stamp",
            "{}",
            null,
            _timeouts.Default,
            cancellationToken);
        await ReportFailureAsync(credentials, SiigoCallOperation.InvoiceStamp, reply, null, siigoInvoiceId, cancellationToken);
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

    // Spec 2026-11-25, anulación §2: de esta lectura depende soltar un documento legal, así que lo que no se entiende no confirma nada.
    // No usa GetAsync: un 4xx no es una excepción, es «no reconocida» y quien llama dice cuál factura.
    public async Task<SiigoInvoiceLookup> GetInvoiceAsync(
        SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(
            credentials,
            HttpMethod.Get,
            $"v1/invoices/{Uri.EscapeDataString(siigoInvoiceId)}",
            null,
            null,
            _timeouts.Default,
            cancellationToken);
        await ReportFailureAsync(credentials, SiigoCallOperation.InvoiceRead, reply, null, siigoInvoiceId, cancellationToken);
        ThrowIfUnanswered(reply);
        if (!reply.Success)
        {
            var errors = SiigoJson.Errors(reply.Body, reply.Status, fallbackToStatus: false);
            if (SiigoApiErrors.IsServiceUnavailable(errors))
            {
                throw UnexpectedAnswer(reply, errors);
            }

            return reply.Status == HttpStatusCode.NotFound
                && errors.Count > 0
                && errors.All(error => SiigoApiErrors.InvoiceNotFoundCodes.Contains(error.Code))
                    ? new SiigoInvoiceLookup.NotFound()
                    : new SiigoInvoiceLookup.Unrecognized((int?)reply.Status);
        }

        using var document = SiigoJson.Parse(reply.Body);
        if (document is { RootElement: var root }
            && string.Equals(SiigoJson.Text(root, "id"), siigoInvoiceId, StringComparison.Ordinal)
            && SiigoJson.Property(root, "annulled") is { ValueKind: JsonValueKind.True or JsonValueKind.False } annulled)
        {
            var stampStatus = SiigoJson.Property(root, "stamp") is { } stamp ? SiigoJson.Text(stamp, "status") : null;
            return new SiigoInvoiceLookup.Found(annulled.GetBoolean(), stampStatus);
        }

        await ReportInvalidResponseAsync(
            credentials, SiigoCallOperation.InvoiceRead, reply, null, cancellationToken, siigoInvoiceId);
        return new SiigoInvoiceLookup.Unrecognized((int?)reply.Status);
    }

    public async Task<byte[]> GetInvoicePdfAsync(SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(
            credentials,
            SiigoCallOperation.InvoicePdf,
            $"v1/invoices/{Uri.EscapeDataString(siigoInvoiceId)}/pdf",
            siigoInvoiceId,
            cancellationToken);
        using var document = SiigoJson.Parse(reply.Body);
        var encoded = document is null ? null : SiigoJson.Text(document.RootElement, "base64");
        try
        {
            return string.IsNullOrWhiteSpace(encoded)
                ? throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, "Siigo answered the invoice PDF without content.")
                : Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, "Siigo answered the invoice PDF with unreadable content.");
        }
    }

    public async Task<IReadOnlyList<SiigoError>> GetStampErrorsAsync(
        SiigoCredentials credentials, string siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await GetAsync(
            credentials,
            SiigoCallOperation.StampErrors,
            $"v1/invoices/{Uri.EscapeDataString(siigoInvoiceId)}/stamp/errors",
            siigoInvoiceId,
            cancellationToken);
        // Sin errores es «la DIAN no rechazó nada», no un rechazo sin causa.
        return SiigoJson.Errors(reply.Body, reply.Status, fallbackToStatus: false);
    }

    // --- Transporte ---

    private Task<IReadOnlyList<T>> CachedAsync<T>(SiigoCredentials credentials, string list, Func<Task<IReadOnlyList<T>>> read) =>
        CachedAsync(credentials, list, refresh: false, read);

    private async Task<IReadOnlyList<T>> CachedAsync<T>(
        SiigoCredentials credentials, string list, bool refresh, Func<Task<IReadOnlyList<T>>> read)
    {
        // La huella va en la llave: editar la conexión con las credenciales de otra cuenta deja el mismo connectionId, y sin ella se
        // servían 5 minutos las listas de la cuenta anterior.
        var key = $"siigo:{credentials.ConnectionId}:{SiigoTokenCache.Fingerprint(credentials)}:{list}";
        if (!refresh && cache.TryGetValue(key, out IReadOnlyList<T>? cached) && cached is not null)
        {
            return cached;
        }

        var value = await read();
        cache.Set(key, value, ListCacheTtl);
        return value;
    }

    private async Task<IReadOnlyList<SiigoOption>> ReadOptionsAsync(SiigoCredentials credentials, string path, CancellationToken cancellationToken) =>
        [
            .. (await ReadAllPagesAsync(credentials, path, cancellationToken))
                .Where(item => SiigoJson.Bool(item, "active", fallback: true))
                .Select(item => new SiigoOption(SiigoJson.Text(item, "id") ?? string.Empty, SiigoJson.NameOf(item), SiigoJson.Text(item, "description")))
                .Where(option => option.Id.Length > 0),
        ];

    /// <summary>
    /// Todas las páginas de una lista. Siigo pagina algunas de a 25 por defecto, y leer sólo la primera dejaba fuera al usuario, la bodega
    /// o el centro de costo 26 (spec 2026-11-20 §4.3). La primera petición va tal cual, sin parámetros de página: los endpoints que
    /// devuelven un arreglo pelado no se tocan. Sólo si la respuesta trae <c>pagination</c> y faltan resultados se piden las siguientes,
    /// con el <c>page_size</c> que Siigo dijo usar, hasta juntar <c>total_results</c> o recibir una página vacía.
    /// <see cref="MaxListPages"/> corta una paginación que no termina.
    /// </summary>
    private async Task<IReadOnlyList<JsonElement>> ReadAllPagesAsync(SiigoCredentials credentials, string path, CancellationToken cancellationToken)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var reply = await GetAsync(credentials, SiigoCallOperation.ListRead, path, null, cancellationToken);
        var items = new List<JsonElement>(SiigoJson.Items(reply.Body));
        var pagination = SiigoJson.Pagination(reply.Body);
        var pageSize = pagination.PageSize is > 0 ? pagination.PageSize.Value : DefaultListPageSize;
        for (var page = 2; page <= MaxListPages && pagination.TotalResults is { } total && items.Count < total; page++)
        {
            reply = await GetAsync(credentials, SiigoCallOperation.ListRead, $"{path}{separator}page={page}&page_size={pageSize}", null, cancellationToken);
            var pageItems = SiigoJson.Items(reply.Body);
            if (pageItems.Count == 0)
            {
                break;
            }

            items.AddRange(pageItems);
            pagination = SiigoJson.Pagination(reply.Body);
        }

        return items;
    }

    // Las lecturas lanzan: antes de lanzar, la falla va a la tabla de fallas (una fila por llamada, y la excepción además llega al log de requests).
    private async Task<Reply> GetAsync(
        SiigoCredentials credentials, SiigoCallOperation operation, string path, string? siigoInvoiceId, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(credentials, HttpMethod.Get, path, null, null, _timeouts.Default, cancellationToken);
        await ReportFailureAsync(credentials, operation, reply, null, siigoInvoiceId, cancellationToken);
        ThrowUnlessSuccess(reply);
        return reply;
    }

    // Spec 2026-11-21: toda llamada que falla deja su curl y la respuesta de Siigo en siigo.call_failures, lance o no el método que sigue
    // (la creación de facturas y el sellado devuelven el rechazo como resultado). Una respuesta buena no deja nada. Una falla de /auth se
    // reporta como Auth, sea quien sea el método que la pidió. Sin Partner-Id no hay petición y no hay nada que reportar.
    private async Task ReportFailureAsync(
        SiigoCredentials credentials,
        SiigoCallOperation operation,
        Reply reply,
        string? idempotencyKey,
        string? siigoInvoiceId,
        CancellationToken cancellationToken)
    {
        // Una respuesta buena trae su captura por si el llamador la declara fallida (ver ReportInvalidResponseAsync); sola no es una falla.
        if (reply.Success)
        {
            return;
        }

        await RecordAsync(
            credentials,
            operation,
            reply.Capture,
            reply.TransientReason ?? (reply.CredentialsRejected ? "credentials_rejected" : $"http_{reply.Capture?.Status}"),
            idempotencyKey,
            siigoInvoiceId,
            cancellationToken);
    }

    // Un 2xx que Siigo contestó sin lo que hacía falta (la factura o el cliente sin id, o la lectura de una factura que no dice si está
    // anulada): no se sabe qué pasó, y es el caso que más pide el curl.
    private Task ReportInvalidResponseAsync(
        SiigoCredentials credentials,
        SiigoCallOperation operation,
        Reply reply,
        string? idempotencyKey,
        CancellationToken cancellationToken,
        string? siigoInvoiceId = null) =>
        RecordAsync(credentials, operation, reply.Capture, "invalid_response", idempotencyKey, siigoInvoiceId, cancellationToken);

    private async Task RecordAsync(
        SiigoCredentials credentials,
        SiigoCallOperation operation,
        FailureCapture? capture,
        string reason,
        string? idempotencyKey,
        string? siigoInvoiceId,
        CancellationToken cancellationToken)
    {
        // Antes de la guarda: cuentan también las fallas sin captura (partner_id_missing) y los hosts sin ISiigoCallFailureLog.
        // Misma regla de operación que la fila: una falla de /auth es Auth, sea quien sea el método que la pidió.
        metrics?.RecordFailure(
            string.Equals(capture?.Path, "auth", StringComparison.Ordinal) ? SiigoCallOperation.Auth : operation, reason);

        if (failures is null || capture is null)
        {
            return;
        }

        await failures.RecordAsync(
            new SiigoCallFailureReport(
                credentials.ConnectionId,
                string.Equals(capture.Path, "auth", StringComparison.Ordinal) ? SiigoCallOperation.Auth : operation,
                capture.Method,
                capture.Path,
                capture.Status,
                reason,
                idempotencyKey,
                siigoInvoiceId,
                capture.RequestCurl,
                capture.ResponseBody),
            cancellationToken);
    }

    // Las lecturas lanzan; la creación de facturas y el sellado no (ver ISiigoApi).
    private static void ThrowUnlessSuccess(Reply reply)
    {
        ThrowIfUnanswered(reply);
        if (!reply.Success)
        {
            // Sólo los códigos de Siigo, nunca sus mensajes: repiten los valores rechazados (documento, dirección, nombre).
            throw UnexpectedAnswer(reply, SiigoJson.Errors(reply.Body, reply.Status, fallbackToStatus: false));
        }
    }

    // Lo que ninguna lectura puede interpretar: Siigo no contestó, o rechazó las credenciales.
    private static void ThrowIfUnanswered(Reply reply)
    {
        if (reply.TransientReason is { } transient)
        {
            throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, $"Siigo did not answer in time or failed ({transient}).");
        }

        if (reply.CredentialsRejected)
        {
            throw new ResourceConflictException(SiigoErrorCodes.ConnectionUnavailable, "Siigo rejected the credentials of this connection.");
        }
    }

    private static UpstreamServiceException UnexpectedAnswer(Reply reply, IReadOnlyList<SiigoError> errors)
    {
        var detail = string.Join(", ", errors.Select(error => error.Code).Distinct().Take(5));
        // Un servicio de Siigo caído tiene su código, para que la pantalla diga que es de Siigo.
        return new UpstreamServiceException(
            SiigoApiErrors.IsServiceUnavailable(errors) ? SiigoErrorCodes.InvoiceSiigoService : "siigo.unexpected_response",
            $"Siigo answered HTTP {(int)reply.Status!.Value}.{(detail.Length > 0 ? $" Error codes: {detail}." : string.Empty)}");
    }

    private async Task<Reply> CallAsync(
        SiigoCredentials credentials,
        HttpMethod method,
        string path,
        string? body,
        string? idempotencyKey,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.PartnerId))
        {
            // Sin Partner-Id Siigo rechaza toda llamada, y esas cuentan para el bloqueo del 80 %: no se llama.
            return Reply.Transient("partner_id_missing");
        }

        for (var attempt = 0; ; attempt++)
        {
            var token = tokens.TryGet(credentials);
            if (token is null)
            {
                var authentication = await AuthenticateAsync(credentials, cancellationToken);
                if (authentication.Failure is { } failure)
                {
                    return failure;
                }

                token = authentication.Token!;
            }

            var reply = await SendAsync(method, path, body, idempotencyKey, token, deadline, cancellationToken);
            if (reply.Status != HttpStatusCode.Unauthorized)
            {
                return reply;
            }

            // 401: el token pudo vencer o revocarse. Se olvida (si sigue siendo ése), se reautentica una vez y se repite lo mismo.
            tokens.Invalidate(credentials, token);
            if (attempt > 0)
            {
                return Reply.Rejected(reply.Capture);
            }
        }
    }

    private async Task<(string? Token, Reply? Failure)> AuthenticateAsync(SiigoCredentials credentials, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { username = credentials.Username, access_key = credentials.AccessKey });
        // Autenticar va con el plazo general aunque lo pida la creación de una factura.
        var reply = await SendAsync(HttpMethod.Post, "auth", body, null, null, _timeouts.Default, cancellationToken);
        if (reply.TransientReason is not null)
        {
            return (null, reply);
        }

        if (!reply.Success)
        {
            // 401, 403 y el resto de los 4xx de /auth son la clave o el usuario; 429 y 5xx ya salieron arriba como transitorios.
            return (null, Reply.Rejected(reply.Capture));
        }

        using var document = SiigoJson.Parse(reply.Body);
        var token = document is null ? null : SiigoJson.Text(document.RootElement, "access_token");
        if (string.IsNullOrEmpty(token))
        {
            return (null, Reply.Transient("auth_without_token", reply.Capture));
        }

        var seconds = SiigoJson.Decimal(document!.RootElement, "expires_in");
        tokens.Set(credentials, token, seconds is > 0 ? TimeSpan.FromSeconds((double)seconds.Value) : DefaultTokenLifetime);
        return (token, null);
    }

    private async Task<Reply> SendAsync(
        HttpMethod method,
        string path,
        string? body,
        string? idempotencyKey,
        string? token,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
        // El plazo de esta petición, enlazado al token de quien llama: cualquiera de los dos la corta.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        try
        {
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

            // Contenido bufereado (el default): el cuerpo se lee dentro del mismo plazo.
            using var response = await httpClient.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            // La captura va con toda respuesta, pero sólo se guarda si el llamador la declara fallida. La respuesta buena de /auth trae el
            // token: su captura lleva el curl y nunca el cuerpo.
            var capture = new FailureCapture(
                method.Method,
                path,
                (int)response.StatusCode,
                SiigoCurl.Build(request, body),
                response.IsSuccessStatusCode && string.Equals(path, "auth", StringComparison.Ordinal) ? null : text);
            return Reply.From(response.StatusCode, text, capture);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Venció el plazo de la llamada, no es una cancelación de quien llama.
            return Reply.Transient("timeout", new FailureCapture(method.Method, path, null, SiigoCurl.Build(request, body), null));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Reply.Transient("network", new FailureCapture(method.Method, path, null, SiigoCurl.Build(request, body), null));
        }
    }

    private Uri BaseUri =>
        new((string.IsNullOrWhiteSpace(options.Value.BaseUrl) ? SiigoOptions.DefaultBaseUrl : options.Value.BaseUrl.Trim()).TrimEnd('/') + "/");

    private static bool IsValidIdempotencyKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= IdempotencyKeyMaxLength && key.All(char.IsAsciiLetterOrDigit);

    /// <summary>Lo que volvió de una llamada: el status y el cuerpo, o el motivo por el que no se pudo (transitorio) o las credenciales
    /// no sirvieron. El cuerpo puede traer datos del cliente: nunca se imprime.</summary>
    private sealed record Reply(
        HttpStatusCode? Status, string Body, string? TransientReason, bool CredentialsRejected, FailureCapture? Capture = null)
    {
        public bool Success => Status is { } status && (int)status is >= 200 and < 300;

        public static Reply From(HttpStatusCode status, string body, FailureCapture? capture = null)
        {
            var code = (int)status;
            // 429 y 5xx (y un 3xx, que no se sigue) no dicen nada de la petición: se reintenta.
            return code is 429 or >= 500 or (>= 300 and < 400) or 408
                ? new Reply(status, string.Empty, $"http_{code}", false, capture)
                : new Reply(status, body, null, false, capture);
        }

        public static Reply Transient(string reason, FailureCapture? capture = null) => new(null, string.Empty, reason, false, capture);

        public static Reply Rejected(FailureCapture? capture = null) => new(null, string.Empty, null, true, capture);

        public override string ToString() => "Reply { }";
    }

    /// <summary>La petición que falló (curl con el token y la <c>access_key</c> enmascarados) y lo que Siigo contestó, antes de que
    /// <see cref="Reply"/> descarte el cuerpo de un 5xx. Lleva datos del cliente: sólo viaja a <see cref="ISiigoCallFailureLog"/>.</summary>
    private sealed record FailureCapture(string Method, string Path, int? Status, string RequestCurl, string? ResponseBody)
    {
        public override string ToString() => "FailureCapture { }";
    }
}

/// <summary>Los plazos de <see cref="SiigoApiClient"/>. No se configuran: existe como tipo para que las pruebas pongan plazos cortos y no
/// esperen de verdad. El contenedor no lo registra, así que el cliente usa <see cref="Production"/>.</summary>
internal sealed record SiigoApiTimeouts(TimeSpan Default, TimeSpan InvoiceCreate)
{
    public static SiigoApiTimeouts Production => new(SiigoApiClient.DefaultTimeout, SiigoApiClient.InvoiceCreateTimeout);
}
