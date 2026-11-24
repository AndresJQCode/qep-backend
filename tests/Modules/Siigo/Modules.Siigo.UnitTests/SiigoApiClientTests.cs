using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BuildingBlocks.Application;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Modules.Siigo.Application;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Api;

namespace Modules.Siigo.UnitTests;

/// <summary>
/// Spec 2026-11-18 §3 y §5.4: el cliente HTTP de Siigo. Se prueba con un handler falso y fixtures con la forma del contrato; ninguna
/// prueba llama a Siigo. El reloj es el del token (vence 5 minutos antes de <c>expires_in</c>), no el del sistema.
/// </summary>
public sealed class SiigoApiClientTests : IDisposable
{
    private const string PartnerId = "qepTest";
    private const string AccessKey = "siigo-access-key-SENTINEL-77";

    private static readonly Guid ConnectionId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly SiigoCredentials Credentials = new(ConnectionId, "api@empresa.test", AccessKey);

    private readonly SiigoHttpStub _api = new();
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SiigoTokenCache _tokens;
    private readonly SiigoApiClient _client;

    public SiigoApiClientTests()
    {
        _tokens = new SiigoTokenCache(_clock);
        _client = NewClient(_api);
    }

    public void Dispose()
    {
        _api.Dispose();
        _cache.Dispose();
    }

    private SiigoApiClient NewClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler, disposeHandler: false),
            Options.Create(new SiigoOptions { BaseUrl = "https://siigo.test", PartnerId = PartnerId }),
            _tokens,
            _cache,
            _failures);

    private readonly RecordingFailureLog _failures = new();

    private sealed class RecordingFailureLog : ISiigoCallFailureLog
    {
        public List<SiigoCallFailureReport> Recorded { get; } = [];

        public Task RecordAsync(SiigoCallFailureReport failure, CancellationToken cancellationToken)
        {
            Recorded.Add(failure);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ARejectedInvoiceLeavesItsReport()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_total_payments","Message":"Cliente Fulano"}]}""");

        await _client.CreateInvoiceAsync(
            Credentials, new SiigoInvoiceRequest("""{"date":"2026-11-18"}"""), "abc123", TestContext.Current.CancellationToken);

        var report = Assert.Single(_failures.Recorded);
        Assert.Equal(SiigoCallOperation.InvoiceCreate, report.Operation);
        Assert.Equal(ConnectionId, report.ConnectionId);
        Assert.Equal(("POST", "v1/invoices", 400, "http_400", "abc123"), (report.Method, report.Path, report.Status, report.Reason, report.IdempotencyKey));
        Assert.Contains("curl.exe -X POST \"https://siigo.test/v1/invoices\"", report.RequestCurl, StringComparison.Ordinal);
        Assert.Contains("Partner-Id: qepTest", report.RequestCurl, StringComparison.Ordinal);
        Assert.Contains("Idempotency-Key: abc123", report.RequestCurl, StringComparison.Ordinal);
        Assert.Contains("""{"date":"2026-11-18"}""", report.RequestCurl, StringComparison.Ordinal);
        Assert.Contains("Bearer <TOKEN>", report.RequestCurl, StringComparison.Ordinal);
        Assert.DoesNotContain("token-one-SENTINEL", report.RequestCurl, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, report.RequestCurl, StringComparison.Ordinal);
        Assert.Contains("invalid_total_payments", report.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransientFailureKeepsTheResponseTheReplyDiscards()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome);
        var report = Assert.Single(_failures.Recorded);
        Assert.Equal(("http_502", 502), (report.Reason, report.Status));
        Assert.Contains("bad gateway", report.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACreatedInvoiceLeavesNothingBecauseItsAuthReplyCarriesTheToken()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Created, Fixture.Read("invoice-created.json"));

        await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(_failures.Recorded);
    }

    [Fact]
    public async Task A401ThatRenewsTheTokenAndSucceedsLeavesNothing()
    {
        _api.OnSequence("/auth", (HttpStatusCode.OK, Fixture.Read("auth.json")), (HttpStatusCode.OK, Fixture.Read("auth-renewed.json")));
        _api.OnSequence(
            "/v1/invoices",
            (HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"expired"}]}"""),
            (HttpStatusCode.Created, Fixture.Read("invoice-created.json")));

        await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Empty(_failures.Recorded);
    }

    // Una lectura con un servicio de Siigo caído no es una respuesta inesperada. Sigue siendo UpstreamServiceException (y no
    // ServiceUnavailableException) porque la tanda reintenta la segunda y aquí no se reintenta: el usuario puede facturar en Siigo.
    [Fact]
    public async Task AReadAnsweredWithAServiceErrorSaysSiigoIsDownWithoutAskingForARetry()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices/abc/pdf", HttpStatusCode.BadRequest, """{"Status":400,"Errors":[{"Code":"documents_service","Message":"x"}]}""");

        var failure = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => _client.GetInvoicePdfAsync(Credentials, "abc", TestContext.Current.CancellationToken));

        Assert.Equal("siigo.invoice.siigo_service", failure.Code);
    }

    [Fact]
    public async Task ASecond401LeavesCredentialsRejected()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"no"}]}""");

        await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        var report = Assert.Single(_failures.Recorded);
        Assert.Equal(("credentials_rejected", SiigoCallOperation.InvoiceCreate, 401), (report.Reason, report.Operation, report.Status));
    }

    [Fact]
    public async Task AFailedAuthIsReportedAsAuthAndHidesTheAccessKey()
    {
        _api.On("/auth", HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"bad key"}]}""");

        await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        var report = Assert.Single(_failures.Recorded);
        Assert.Equal(SiigoCallOperation.Auth, report.Operation);
        Assert.Equal("auth", report.Path);
        Assert.Contains("<ACCESS_KEY>", report.RequestCurl, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, report.RequestCurl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrownReadFailureIsReportedOnceAndTheExceptionCarriesNoCurl()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/customers", HttpStatusCode.UnprocessableEntity, """{"Errors":[{"Code":"invalid_field","Message":"x"}]}""");

        var exception = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => _client.FindCustomerAsync(Credentials, "900123456", TestContext.Current.CancellationToken));

        Assert.Empty(exception.Data);
        var report = Assert.Single(_failures.Recorded);
        Assert.Equal(SiigoCallOperation.CustomerFind, report.Operation);
        Assert.Contains("/v1/customers?identification=900123456", report.RequestCurl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvoiceAnsweredWithoutIdLeavesAnInvalidResponseReport()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Created, """{"name":"FV-sin-id"}""");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome);
        var report = Assert.Single(_failures.Recorded);
        Assert.Equal(("invalid_response", SiigoCallOperation.InvoiceCreate, 201, "abc123"), (report.Reason, report.Operation, report.Status, report.IdempotencyKey));
        Assert.Contains("FV-sin-id", report.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthAnsweredWithoutTokenIsReportedWithoutItsBody()
    {
        _api.On("/auth", HttpStatusCode.OK, """{"expires_in":86400,"note":"SENTINEL-BODY"}""");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome);
        var report = Assert.Single(_failures.Recorded);
        Assert.Equal((SiigoCallOperation.Auth, "auth_without_token"), (report.Operation, report.Reason));
        Assert.Null(report.ResponseBody);
        Assert.Contains("<ACCESS_KEY>", report.RequestCurl, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessKey, report.RequestCurl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStampFailureCarriesTheSiigoInvoiceId()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices/inv-77/stamp", HttpStatusCode.BadRequest, """{"Errors":[{"Code":"dian_rejected","Message":"x"}]}""");

        await _client.StampInvoiceAsync(Credentials, "inv-77", TestContext.Current.CancellationToken);

        var report = Assert.Single(_failures.Recorded);
        Assert.Equal((SiigoCallOperation.InvoiceStamp, "inv-77"), (report.Operation, report.SiigoInvoiceId));
    }

    [Fact]
    public async Task EveryCallCarriesPartnerIdAndBearerToken()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/taxes", HttpStatusCode.OK, Fixture.Read("taxes.json"));

        await _client.ListTaxesAsync(Credentials, TestContext.Current.CancellationToken);

        var auth = _api.Requests.ElementAt(0);
        Assert.Equal("/auth", auth.Path);
        Assert.Equal(PartnerId, auth.Header("Partner-Id"));
        Assert.Contains("\"access_key\"", auth.Body, StringComparison.Ordinal);
        var taxes = _api.Requests.ElementAt(1);
        Assert.Equal(PartnerId, taxes.Header("Partner-Id"));
        Assert.True(taxes.Header("Authorization") == "Bearer token-one-SENTINEL", "The token must travel as a bearer token.");
    }

    [Fact]
    public async Task TheTokenIsReusedUntilFiveMinutesBeforeExpiry()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/document-types", HttpStatusCode.OK, Fixture.Read("document-types.json"));
        _api.On("/v1/customers", HttpStatusCode.OK, Fixture.Read("customers-empty.json"));

        await _client.ListDocumentTypesAsync(Credentials, TestContext.Current.CancellationToken);
        await _client.FindCustomerAsync(Credentials, "900123456", TestContext.Current.CancellationToken);
        Assert.Single(_api.Requests, request => request.Path == "/auth");

        // 86400 s de vida menos el margen de 5 minutos: un segundo antes todavía sirve, al vencer el margen se autentica de nuevo.
        _clock.Now += TimeSpan.FromSeconds(86400 - 300 - 1);
        await _client.FindCustomerAsync(Credentials, "900123456", TestContext.Current.CancellationToken);
        Assert.Single(_api.Requests, request => request.Path == "/auth");

        _clock.Now += TimeSpan.FromSeconds(2);
        await _client.FindCustomerAsync(Credentials, "900123456", TestContext.Current.CancellationToken);
        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/auth"));
    }

    [Fact]
    public async Task TheTokenOfOneConnectionIsNotUsedForAnother()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/customers", HttpStatusCode.OK, Fixture.Read("customers-empty.json"));

        await _client.FindCustomerAsync(Credentials, "1", TestContext.Current.CancellationToken);
        await _client.FindCustomerAsync(Credentials with { ConnectionId = Guid.CreateVersion7() }, "1", TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/auth"));
    }

    // Review Focus 4: el 401 de una creación no la pierde ni la duplica: se reautentica y se repite con la MISMA clave.
    [Fact]
    public async Task A401RenewsTheTokenOnceAndRetriesWithTheSameKey()
    {
        _api.OnSequence("/auth", (HttpStatusCode.OK, Fixture.Read("auth.json")), (HttpStatusCode.OK, Fixture.Read("auth-renewed.json")));
        _api.OnSequence(
            "/v1/invoices",
            (HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"expired"}]}"""),
            (HttpStatusCode.Created, Fixture.Read("invoice-created.json")));

        var outcome = await _client.CreateInvoiceAsync(
            Credentials, new SiigoInvoiceRequest("""{"date":"2026-11-18"}"""), "abc123def456", TestContext.Current.CancellationToken);

        var created = Assert.IsType<SiigoInvoiceOutcome.Created>(outcome);
        Assert.Equal("FV-1-123", created.Name);
        var invoices = _api.Requests.Where(request => request.Path == "/v1/invoices").ToList();
        Assert.Equal(2, invoices.Count);
        Assert.All(invoices, request => Assert.Equal("abc123def456", request.Header("Idempotency-Key")));
        Assert.Equal(invoices[0].Body, invoices[1].Body);
        Assert.Equal("Bearer token-one-SENTINEL", invoices[0].Header("Authorization"));
        Assert.Equal("Bearer token-two-SENTINEL", invoices[1].Header("Authorization"));
        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/auth"));
    }

    [Fact]
    public async Task ASecond401IsRejectedAsCredentialsRejected()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Unauthorized, """{"Errors":[{"Code":"unauthorized","Message":"no"}]}""");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<SiigoInvoiceOutcome.Rejected>(outcome);
        Assert.Equal(SiigoApiErrors.CredentialsRejected, Assert.Single(rejected.Errors).Code);
        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/v1/invoices"));
    }

    [Fact]
    public async Task RejectedCredentialsOnAuthAreRejectedWithoutCallingTheApi()
    {
        _api.On("/auth", HttpStatusCode.Unauthorized, "{}");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal(SiigoApiErrors.CredentialsRejected, Assert.Single(Assert.IsType<SiigoInvoiceOutcome.Rejected>(outcome).Errors).Code);
        Assert.DoesNotContain(_api.Requests, request => request.Path == "/v1/invoices");
    }

    [Fact]
    public async Task InvalidTotalPaymentsIsRejectedWithTheExpectedTotal()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.BadRequest, Fixture.Read("invoice-invalid-total-payments.json"));

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<SiigoInvoiceOutcome.Rejected>(outcome);
        Assert.Equal(288490.5m, rejected.ExpectedPaymentsTotal);
        Assert.Equal("invalid_total_payments", Assert.Single(rejected.Errors).Code);
    }

    [Fact]
    public async Task AnyOtherValidationRejectionKeepsAllItsErrorsAndNoTotal()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.UnprocessableEntity, Fixture.Read("invoice-rejected.json"));

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<SiigoInvoiceOutcome.Rejected>(outcome);
        Assert.Equal(["invalid_code", "invalid_seller"], rejected.Errors.Select(error => error.Code));
        Assert.Null(rejected.ExpectedPaymentsTotal);
    }

    [Theory]
    [InlineData("El total de pagos debe ser 288490.5", 288490.5)]
    [InlineData("Expected total: 1234 (payments 1000)", 1000.0)]
    [InlineData("El total de pagos debe ser 288,490.50", 288490.5)]
    [InlineData("El total de pagos debe ser 1,234,567", 1234567.0)]
    [InlineData("El total de pagos debe ser 288.490,50", 288490.5)]
    [InlineData("sin numeros", null)]
    public void TheExpectedTotalIsTheLastNumberOfTheMessage(string message, double? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected, SiigoApiErrors.ParseExpectedPaymentsTotal(message));

    [Fact]
    public async Task AServerErrorIsTransient()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.ServiceUnavailable, "{}");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("http_503", Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public async Task TooManyRequestsIsTransient()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.TooManyRequests, "{}");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("http_429", Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public async Task ATimeoutIsTransient()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.Throw("/v1/invoices", new TaskCanceledException("timed out", new TimeoutException()));

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("timeout", Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public async Task ANetworkFailureIsTransient()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.Throw("/v1/invoices", new HttpRequestException("connection refused"));

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("network", Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome).Reason);
    }

    // Una conexión que se cae a mitad del cuerpo no es un fallo de Siigo: la factura pudo crearse, así que queda para reintentar.
    [Fact]
    public async Task AConnectionResetWhileReadingTheBodyIsTransient()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.Throw("/v1/invoices", new IOException("reset"));

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("network", Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public async Task ACreatedInvoiceReadsIdNameAndTotal()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Created, Fixture.Read("invoice-created.json"));

        var outcome = await _client.CreateInvoiceAsync(
            Credentials, new SiigoInvoiceRequest("""{"date":"2026-11-18"}"""), "abc123", TestContext.Current.CancellationToken);

        var created = Assert.IsType<SiigoInvoiceOutcome.Created>(outcome);
        Assert.Equal("d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f", created.Id);
        Assert.Equal("FV-1-123", created.Name);
        Assert.Equal(288490.5m, created.Total);
        Assert.Null(created.Cufe);
        var request = Assert.Single(_api.Requests, request => request.Path == "/v1/invoices");
        Assert.Equal("""{"date":"2026-11-18"}""", request.Body);
        Assert.Equal("abc123", request.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task ACreatedInvoiceThatCameStampedCarriesItsCufe()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Created, Fixture.Read("invoice-created-stamped.json"));

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("cufe-SENTINEL-123", Assert.IsType<SiigoInvoiceOutcome.Created>(outcome).Cufe);
    }

    // Un 2xx que no trae id es «no sé si se creó»: reintentar con la misma clave es lo seguro, nunca dar la factura por creada.
    [Fact]
    public async Task ACreatedAnswerWithoutAnIdIsTransient()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices", HttpStatusCode.Created, "{}");

        var outcome = await _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), "abc123", TestContext.Current.CancellationToken);

        Assert.Equal("invalid_response", Assert.IsType<SiigoInvoiceOutcome.Transient>(outcome).Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("with-dash")]
    [InlineData("0123456789012345678901234567890")]
    [InlineData("acentó")]
    public async Task AnInvalidIdempotencyKeyIsABugAndNothingIsSent(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _client.CreateInvoiceAsync(Credentials, new SiigoInvoiceRequest("{}"), key, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Requests);
    }

    [Fact]
    public async Task DocumentTypesAreAskedForSalesInvoicesAndInactiveOnesAreLeftOut()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/document-types", HttpStatusCode.OK, Fixture.Read("document-types.json"));

        var options = await _client.ListDocumentTypesAsync(Credentials, TestContext.Current.CancellationToken);

        Assert.Equal("?type=FV", _api.Requests.Last().Query);
        var option = Assert.Single(options);
        Assert.Equal(new SiigoOption("24446", "Factura de venta", "Factura de venta electrónica"), option);
    }

    [Fact]
    public async Task PaymentTypesAreAskedForSalesInvoices()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/payment-types", HttpStatusCode.OK, Fixture.Read("document-types.json"));

        await _client.ListPaymentTypesAsync(Credentials, TestContext.Current.CancellationToken);

        Assert.Equal("?document_type=FV", _api.Requests.Last().Query);
    }

    [Fact]
    public async Task UsersAreReadFromAPagedAnswerAndNamedByPersonOrUsername()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/users", HttpStatusCode.OK, Fixture.Read("users.json"));

        var users = await _client.ListUsersAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);

        // El 631 está inactivo: no se ofrece ni puede ser vendedor.
        Assert.Equal(
            [new SiigoUser("629", "Ana Gomez", "ventas@empresa.test"), new SiigoUser("630", "otro@empresa.test", "otro@empresa.test")],
            users);
    }

    [Fact]
    public async Task ListsFollowEveryPageUntilTotalResults()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        var first = string.Join(",", Enumerable.Range(1, 25).Select(id => $$"""{"id":{{id}},"first_name":"U{{id}}","email":"u{{id}}@empresa.test","active":true}"""));
        _api.OnSequence(
            "/v1/users",
            (HttpStatusCode.OK, $$"""{"pagination":{"page":1,"page_size":25,"total_results":26},"results":[{{first}}]}"""),
            (HttpStatusCode.OK, """{"pagination":{"page":2,"page_size":25,"total_results":26},"results":[{"id":9999,"first_name":"Ultima","email":"ultima@empresa.test","active":true}]}"""));

        var users = await _client.ListUsersAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(26, users.Count);
        Assert.Equal("9999", users[^1].Id);
        // La primera petición va tal cual (como antes del arreglo); las siguientes piden la página con el tamaño que Siigo dijo usar.
        Assert.Equal(
            ["", "?page=2&page_size=25"],
            _api.Requests.Where(request => request.Path == "/v1/users").Select(request => request.Query));
    }

    [Fact]
    public async Task ARefreshReadsTheUsersAgainAndKeepsThemCached()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/users", HttpStatusCode.OK, Fixture.Read("users.json"));

        await _client.ListUsersAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        await _client.ListUsersAsync(Credentials, refresh: true, TestContext.Current.CancellationToken);
        await _client.ListUsersAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/v1/users"));
    }

    [Fact]
    public async Task ListsWithoutPaginationAreReadOnce()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/warehouses", HttpStatusCode.OK, """[{"id":3,"name":"Bodega central","active":true}]""");

        var warehouses = await _client.ListWarehousesAsync(Credentials, TestContext.Current.CancellationToken);

        Assert.Equal([new SiigoOption("3", "Bodega central")], warehouses);
        Assert.Single(_api.Requests, request => request.Path == "/v1/warehouses");
    }

    [Fact]
    public async Task APathWithQueryKeepsItAndAddsThePage()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.OnSequence(
            "/v1/payment-types",
            (HttpStatusCode.OK, """{"pagination":{"page":1,"page_size":1,"total_results":2},"results":[{"id":1,"name":"Uno","active":true}]}"""),
            (HttpStatusCode.OK, """{"pagination":{"page":2,"page_size":1,"total_results":2},"results":[{"id":2,"name":"Dos","active":true}]}"""));

        var types = await _client.ListPaymentTypesAsync(Credentials, TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2"], types.Select(type => type.Id));
        Assert.Equal(
            ["?document_type=FV", "?document_type=FV&page=2&page_size=1"],
            _api.Requests.Where(request => request.Path == "/v1/payment-types").Select(request => request.Query));
    }

    [Fact]
    public async Task TaxesCarryTypeAndPercentage()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/taxes", HttpStatusCode.OK, Fixture.Read("taxes.json"));

        var taxes = await _client.ListTaxesAsync(Credentials, TestContext.Current.CancellationToken);

        Assert.Equal(3, taxes.Count);
        Assert.Equal(new SiigoTaxOption("13156", "IVA 19%", "IVA", 19.0m, true), taxes[0]);
        Assert.Equal(9.66m, taxes[1].Percentage);
        Assert.False(taxes[2].Active);
    }

    [Fact]
    public async Task AListIsCachedPerConnectionForFiveMinutes()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/users", HttpStatusCode.OK, Fixture.Read("users.json"));

        await _client.ListUsersAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        await _client.ListUsersAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        Assert.Single(_api.Requests, request => request.Path == "/v1/users");

        await _client.ListUsersAsync(Credentials with { ConnectionId = Guid.CreateVersion7() }, refresh: false, TestContext.Current.CancellationToken);
        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/v1/users"));
    }

    [Fact]
    public async Task ProductsComeInPagesAndSearchByCode()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/products", HttpStatusCode.OK, Fixture.Read("products-page.json"));

        var page = await _client.SearchProductsAsync(Credentials, "AB-1", 2, TestContext.Current.CancellationToken);

        Assert.Contains("code=AB-1", _api.Requests.Last().Query, StringComparison.Ordinal);
        Assert.Contains("page=2", _api.Requests.Last().Query, StringComparison.Ordinal);
        Assert.Equal(2, page.Page);
        Assert.Equal(230, page.TotalResults);
        Assert.Equal(100, page.PageSize);
        Assert.Equal(new SiigoProductOption("7a1f0c33-0000-4000-8000-000000000001", "AB-1", "Producto uno", true), page.Items[0]);
        Assert.False(page.Items[1].Active);
    }

    private static string ProductsJson(int page, int total, params (string Id, string Code, string Name, bool Active)[] items) =>
        "{\"pagination\":{\"page\":" + page + ",\"page_size\":2,\"total_results\":" + total + "},\"results\":["
        + string.Join(",", items.Select(item =>
            $"{{\"id\":\"{item.Id}\",\"code\":\"{item.Code}\",\"name\":\"{item.Name}\",\"active\":{(item.Active ? "true" : "false")}}}"))
        + "]}";

    [Fact]
    public async Task ActiveProductsWalkEveryPageAndKeepOnlyTheActiveOnes()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.OnSequence(
            "/v1/products",
            (HttpStatusCode.OK, ProductsJson(1, 3, ("p1", "AB-1", "Uno", true), ("p2", "AB-2", "Dos", false))),
            (HttpStatusCode.OK, ProductsJson(2, 3, ("p3", "AB-3", "Tres", true))));

        var products = await _client.ListActiveProductsAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(["AB-1", "AB-3"], products.Select(product => product.Code));
        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/v1/products"));
    }

    [Fact]
    public async Task ActiveProductsAreCachedAndRefreshReadsSiigoAgain()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.OnSequence(
            "/v1/products",
            (HttpStatusCode.OK, ProductsJson(1, 1, ("p1", "AB-1", "Uno", true))),
            (HttpStatusCode.OK, ProductsJson(1, 2, ("p1", "AB-1", "Uno", true), ("p9", "NEW-9", "Recién creado", true))));

        var first = await _client.ListActiveProductsAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        var cached = await _client.ListActiveProductsAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        Assert.Single(first);
        Assert.Same(first, cached);
        Assert.Single(_api.Requests, request => request.Path == "/v1/products");

        // Un producto creado en Siigo hace un momento tiene que aparecer: `refresh` se salta la caché y la repuebla.
        var refreshed = await _client.ListActiveProductsAsync(Credentials, refresh: true, TestContext.Current.CancellationToken);
        Assert.Equal(["AB-1", "NEW-9"], refreshed.Select(product => product.Code));
        var afterRefresh = await _client.ListActiveProductsAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        Assert.Same(refreshed, afterRefresh);
        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/v1/products"));
    }

    [Fact]
    public async Task ActiveProductsAreCachedPerConnection()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/products", HttpStatusCode.OK, ProductsJson(1, 1, ("p1", "AB-1", "Uno", true)));

        await _client.ListActiveProductsAsync(Credentials, refresh: false, TestContext.Current.CancellationToken);
        await _client.ListActiveProductsAsync(
            Credentials with { ConnectionId = Guid.CreateVersion7() }, refresh: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Requests.Count(request => request.Path == "/v1/products"));
    }

    [Fact]
    public async Task AFoundCustomerIsReturnedAndAnEmptySearchIsNull()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.OnSequence(
            "/v1/customers",
            (HttpStatusCode.OK, Fixture.Read("customers-found.json")),
            (HttpStatusCode.OK, Fixture.Read("customers-empty.json")));

        var found = await _client.FindCustomerAsync(Credentials, "900123456", TestContext.Current.CancellationToken);
        var missing = await _client.FindCustomerAsync(Credentials, "1", TestContext.Current.CancellationToken);

        Assert.Equal(new SiigoCustomerRef("c0ffee00-0000-4000-8000-000000000009", "900123456"), found);
        Assert.Null(missing);
        Assert.Equal("?identification=900123456", _api.Requests.Where(request => request.Path == "/v1/customers").First().Query);
    }

    [Fact]
    public async Task ACustomerIsCreatedWithTheContractShapeAndNeverCached()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/customers", HttpStatusCode.Created, Fixture.Read("customer-created.json"));
        var request = new SiigoCustomerRequest(
            "Company", "31", "900123456", ["ACME SAS"], "Calle 1 # 2-3", "Co", "11", "11001", "3001234567", "Ana", "Gomez", "ana@acme.test");

        var created = await _client.CreateCustomerAsync(Credentials, request, TestContext.Current.CancellationToken);

        Assert.Equal("c0ffee00-0000-4000-8000-00000000000a", created.Id);
        var sent = _api.Requests.Single(value => value.Path == "/v1/customers");
        Assert.Equal(HttpMethod.Post, sent.Method);
        using var body = System.Text.Json.JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        Assert.Equal("Customer", root.GetProperty("type").GetString());
        Assert.Equal("Company", root.GetProperty("person_type").GetString());
        Assert.Equal("31", root.GetProperty("id_type").GetProperty("code").GetString());
        Assert.Equal("900123456", root.GetProperty("identification").GetString());
        Assert.True(root.GetProperty("active").GetBoolean());
        Assert.Equal("ACME SAS", root.GetProperty("name")[0].GetString());
        var city = root.GetProperty("address").GetProperty("city");
        Assert.Equal("Co", city.GetProperty("country_code").GetString());
        Assert.Equal("11", city.GetProperty("state_code").GetString());
        Assert.Equal("11001", city.GetProperty("city_code").GetString());
        Assert.Equal("3001234567", root.GetProperty("phones")[0].GetProperty("number").GetString());
        Assert.Equal("ana@acme.test", root.GetProperty("contacts")[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task ARejectedCustomerNamesTheSiigoErrorCodesButNeverTheirMessages()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On(
            "/v1/customers",
            HttpStatusCode.BadRequest,
            """{"Errors":[{"Code":"invalid_city","Message":"Ciudad 99999 de CLIENTE-SENTINEL no existe"}]}""");
        var request = new SiigoCustomerRequest(
            "Person", "13", "123", ["Ana", "Gomez"], null, "Co", "11", "99999", null, "Ana", "Gomez", null);

        var error = await Assert.ThrowsAsync<UpstreamServiceException>(
            () => _client.CreateCustomerAsync(Credentials, request, TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 400", error.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_city", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStampIsPostedToTheInvoiceAndItsErrorsAreRead()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices/inv-1/stamp", HttpStatusCode.OK, """{"stamp":{"cufe":"cufe-SENTINEL-9","status":"Accepted"}}""");
        _api.On("/v1/invoices/inv-2/stamp/errors", HttpStatusCode.OK, Fixture.Read("stamp-errors.json"));

        var stamped = await _client.StampInvoiceAsync(Credentials, "inv-1", TestContext.Current.CancellationToken);
        var errors = await _client.GetStampErrorsAsync(Credentials, "inv-2", TestContext.Current.CancellationToken);

        Assert.Equal("cufe-SENTINEL-9", Assert.IsType<SiigoStampOutcome.Stamped>(stamped).Cufe);
        Assert.Equal(new SiigoError("stamp_rejected", "El NIT del adquiriente no es valido"), Assert.Single(errors));
    }

    [Fact]
    public async Task TheInvoicePdfIsReadFromItsBase64Field()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices/inv-1/pdf", HttpStatusCode.OK, """{"base64":"JVBERi0xLjQ="}""");

        var pdf = await _client.GetInvoicePdfAsync(Credentials, "inv-1", TestContext.Current.CancellationToken);

        Assert.Equal("%PDF-1.4"u8.ToArray(), pdf);
    }

    [Fact]
    public async Task AnInvoicePdfAnswerWithoutContentRaisesServiceUnavailable()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/invoices/inv-1/pdf", HttpStatusCode.OK, "{}");

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => _client.GetInvoicePdfAsync(Credentials, "inv-1", TestContext.Current.CancellationToken));

        Assert.Equal("siigo.unavailable", error.Code);
    }

    [Fact]
    public async Task AFailedListCallThatIsTransientRaisesServiceUnavailable()
    {
        _api.On("/auth", HttpStatusCode.OK, Fixture.Read("auth.json"));
        _api.On("/v1/taxes", HttpStatusCode.BadGateway, "{}");

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => _client.ListTaxesAsync(Credentials, TestContext.Current.CancellationToken));

        Assert.Equal("siigo.unavailable", error.Code);
    }

    [Fact]
    public async Task RejectedCredentialsOnAListRaiseConnectionUnavailable()
    {
        _api.On("/auth", HttpStatusCode.Forbidden, "{}");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => _client.ListTaxesAsync(Credentials, TestContext.Current.CancellationToken));

        Assert.Equal("siigo.connection_unavailable", error.Code);
    }

    [Fact]
    public void TheCredentialsNeverPrintTheAccessKey() =>
        Assert.DoesNotContain(AccessKey, Credentials.ToString(), StringComparison.Ordinal);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

internal sealed record SiigoStubRequest(HttpMethod Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Un handler que contesta por ruta, en orden si la ruta tiene una secuencia, y anota todo lo que le llega.</summary>
internal sealed class SiigoHttpStub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _last = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SiigoStubRequest> _requests = new();

    public IReadOnlyCollection<SiigoStubRequest> Requests => _requests;

    public void On(string path, HttpStatusCode status, string body) =>
        _last[path] = () => Respond(status, body);

    public void OnSequence(string path, params (HttpStatusCode Status, string Body)[] answers)
    {
        var queue = new ConcurrentQueue<Func<HttpResponseMessage>>();
        foreach (var (status, body) in answers)
        {
            queue.Enqueue(() => Respond(status, body));
        }

        _routes[path] = queue;
    }

    public void Throw(string path, Exception exception) => _last[path] = () => throw exception;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue(new SiigoStubRequest(request.Method, path, request.RequestUri.Query, headers, body));

        if (_routes.TryGetValue(path, out var queue) && queue.TryDequeue(out var next))
        {
            return next();
        }

        return _last.TryGetValue(path, out var answer)
            ? answer()
            : throw new InvalidOperationException($"No stub for {request.Method} {path}.");
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
