using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Automations.Application;
using Modules.Automations.Application.Http;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;
using Modules.Automations.Infrastructure.Http;
using static Modules.Automations.UnitTests.HttpBlockRequestTests;

namespace Modules.Automations.UnitTests;

/// <summary>
/// Spec 2026-10-21 D-H4..D-H8 y fallos P2-R11 y P2-R31: el ejecutor del bloque HTTP contra un handler de mentira. La clave va en el header
/// de la conexión y nunca en un log; cada llamada deja su fila en <c>http_calls</c> salvo cuando no hay conexión que registrar.
/// </summary>
public sealed class HttpBlockExecutorTests
{
    internal static readonly Guid ConnectionId = Guid.CreateVersion7();

    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static readonly Guid RunId = Guid.CreateVersion7();

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 15, 0, 0, TimeSpan.Zero);

    private const string Key = "K-SENTINEL-9f3";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpCallContext Context => new(TenantId, RunId, null, "http1");

    private static RunVariables Variables()
    {
        var variables = RunVariables.Empty();
        variables.Set("cedula", "10203");
        return variables;
    }

    [Fact]
    public async Task SendsTheKeyHeaderAndMapsTheResponse()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, """{"data":[{"id":7}]}"""));

        var result = await bed.Executor().ExecuteAsync(
            Context, Node("/customers", query: [new KeyValue("identificationNumber", "{cedula}")], mapping: [new HttpMapping("data[0]", "cliente")]),
            Variables(), Ct);

        Assert.True(result.Ok);
        Assert.Equal(200, result.Status);
        Assert.Equal("ok", result.Outcome);
        Assert.Null(result.ErrorCode);
        Assert.Equal("7", result.Mapped.TextOf("cliente.id"));
        var request = Assert.Single(bed.Handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.clasic.test/v1/customers?identificationNumber=10203", request.Uri.AbsoluteUri);
        Assert.Equal(Key, request.Headers["x-api-key"]);
        var entry = Assert.Single(bed.Log.Entries);
        Assert.Equal(
            (TenantId, (Guid?)RunId, (Guid?)null, "http1", ConnectionId, "GET", "/customers", (int?)200, "ok", (string?)null, Now),
            (entry.TenantId, entry.RunId, entry.SimulationId, entry.NodeId, entry.ConnectionId, entry.Method, entry.Path, entry.StatusCode,
                entry.Outcome, entry.ErrorCode, entry.CalledAt));
    }

    [Fact]
    public async Task QueryValuesAreEscaped()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var variables = Variables();
        variables.Set("texto", "a b&c");

        await bed.Executor().ExecuteAsync(Context, Node(query: [new KeyValue("q", "{texto}")]), variables, Ct);

        Assert.Equal("?q=a%20b%26c", Assert.Single(bed.Handler.Requests).Uri.Query);
    }

    /// <summary>P2-R31: un valor del cliente con <c>/</c>, <c>?</c> o <c>#</c> se queda dentro de su segmento.</summary>
    [Fact]
    public async Task PathValuesAreEscapedInTheRequest()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var variables = Variables();
        variables.Set("id", "../x?y#z");

        await bed.Executor().ExecuteAsync(Context, Node("/customers/{id}"), variables, Ct);

        var request = Assert.Single(bed.Handler.Requests);
        Assert.Equal("/v1/customers/..%2Fx%3Fy%23z", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal("/customers/..%2Fx%3Fy%23z", Assert.Single(bed.Log.Entries).Path);
    }

    /// <summary>P2-R31: un valor que deja un segmento <c>..</c> no sale (System.Uri lo resolvería hacia arriba de la URL base).</summary>
    [Fact]
    public async Task ADotSegmentFromAValueIsRejectedWithoutACall()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var variables = Variables();
        variables.Set("id", "..");

        var result = await bed.Executor().ExecuteAsync(Context, Node("/customers/{id}"), variables, Ct);

        Assert.Equal(("url_rejected", "path_dot_segment", false), (result.Outcome, result.ErrorCode, result.Ok));
        Assert.Empty(bed.Handler.Requests);
        Assert.Equal("url_rejected", Assert.Single(bed.Log.Entries).Outcome);
    }

    /// <summary>P2-R31 (y P2-R30): una conexión guardada antes de la regla no puede mandar la clave en <c>Host</c>.</summary>
    [Fact]
    public async Task AReservedAuthHeaderIsConnectionUnavailableWithoutACallOrALog()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"), authHeader: "Host");

        var result = await bed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);

        Assert.Equal(("connection_unavailable", false), (result.Outcome, result.Ok));
        Assert.Equal(["Host"], bed.Policy.CheckedHeaders);
        Assert.Empty(bed.Handler.Requests);
        Assert.Empty(bed.Log.Entries);
    }

    [Fact]
    public async Task ANon2xxIsErrorStatusUnlessOkWhenAllowsIt()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.NotFound, """{"message":"no"}"""));

        var failed = await bed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);
        var allowed = await bed.Executor().ExecuteAsync(Context, Node(okWhen: [200, 404]), Variables(), Ct);

        Assert.Equal((false, 404, "error_status", "http_404"), (failed.Ok, failed.Status, failed.Outcome, failed.ErrorCode));
        Assert.Equal((true, 404, "ok"), (allowed.Ok, allowed.Status, allowed.Outcome));
        Assert.Equal([("error_status", "http_404"), ("ok", null)], bed.Log.Entries.Select(entry => (entry.Outcome, entry.ErrorCode)));
    }

    [Fact]
    public async Task ATimeoutIsTimeout()
    {
        var bed = new Bed(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return Json(HttpStatusCode.OK, "{}");
        });

        var result = await bed.Executor().ExecuteAsync(Context, Node(timeoutSeconds: 1), Variables(), Ct);

        Assert.Equal(("timeout", false), (result.Outcome, result.Ok));
        var entry = Assert.Single(bed.Log.Entries);
        Assert.Equal("timeout", entry.Outcome);
        Assert.True(entry.DurationMs >= 1000, $"Duró {entry.DurationMs} ms.");
    }

    [Fact]
    public async Task ANetworkFailureIsNetwork()
    {
        var bed = new Bed(_ => throw new HttpRequestException("connection refused"));

        var result = await bed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);

        Assert.Equal(("network", false), (result.Outcome, result.Ok));
        Assert.Equal("network", Assert.Single(bed.Log.Entries).Outcome);
    }

    /// <summary>D-H2: el host resolvió a un rango interno al conectar (la política lo reconoce en la excepción).</summary>
    [Fact]
    public async Task AConnectRejectionIsUrlRejected()
    {
        var bed = new Bed(_ => throw new HttpRequestException("rejected", new InvalidOperationException(StubPolicy.ConnectRejection)));

        var result = await bed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);

        Assert.Equal(("url_rejected", "resolved_to_forbidden_range"), (result.Outcome, result.ErrorCode));
        Assert.Equal("url_rejected", Assert.Single(bed.Log.Entries).Outcome);
    }

    [Fact]
    public async Task ABodyOverOneMegabyteIsTooLarge()
    {
        var big = new string('a', (1024 * 1024) + 10);
        var declared = new Bed(_ => Json(HttpStatusCode.OK, $"\"{big}\""));
        var streamed = new Bed(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnsizedStream(Encoding.UTF8.GetBytes(big))) });
        var mapping = new[] { new HttpMapping("data", "x") };

        var byLength = await declared.Executor().ExecuteAsync(Context, Node(mapping: mapping), Variables(), Ct);
        var byStream = await streamed.Executor().ExecuteAsync(Context, Node(mapping: mapping), Variables(), Ct);

        Assert.Equal(("response_too_large", false), (byLength.Outcome, byLength.Ok));
        Assert.Equal(("response_too_large", false), (byStream.Outcome, byStream.Ok));
        Assert.Equal("response_too_large", Assert.Single(declared.Log.Entries).Outcome);
        Assert.Equal("response_too_large", Assert.Single(streamed.Log.Entries).Outcome);
    }

    [Fact]
    public async Task ANonJsonBodyWithAMappingIsMappingFailed()
    {
        var bed = new Bed(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") });

        var result = await bed.Executor().ExecuteAsync(Context, Node(mapping: [new HttpMapping("data", "x")]), Variables(), Ct);

        Assert.Equal((false, 200, "mapping_failed", "body_not_json"), (result.Ok, result.Status, result.Outcome, result.ErrorCode));
        Assert.Empty(result.Mapped.Snapshot());
        Assert.Equal(("mapping_failed", "body_not_json"), (Assert.Single(bed.Log.Entries).Outcome, bed.Log.Entries[0].ErrorCode));
    }

    [Fact]
    public async Task AMissingMappingPathIsNullNotAFailure()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, """{"data":[]}"""));

        var result = await bed.Executor().ExecuteAsync(
            Context, Node(mapping: [new HttpMapping("data[0]", "cliente"), new HttpMapping("data", "todos")]), Variables(), Ct);

        Assert.True(result.Ok);
        var mapped = result.Mapped.Snapshot();
        Assert.True(mapped.ContainsKey("cliente"));
        Assert.Null(mapped["cliente"]);
        Assert.Equal("[]", result.Mapped.TextOf("todos"));
    }

    [Fact]
    public async Task WithoutMappingNothingIsStored()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, """{"data":[{"id":7}]}"""));

        var result = await bed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);

        Assert.True(result.Ok);
        Assert.Empty(result.Mapped.Snapshot());
    }

    [Fact]
    public async Task ARejectedUrlIsAnErrorOutcomeWithoutACall()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"), rejection: "scheme_not_https");

        var result = await bed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);

        Assert.Equal(("url_rejected", "scheme_not_https", false), (result.Outcome, result.ErrorCode, result.Ok));
        Assert.Empty(bed.Handler.Requests);
        var entry = Assert.Single(bed.Log.Entries);
        Assert.Equal(("url_rejected", (int?)null, "/customers"), (entry.Outcome, entry.StatusCode, entry.Path));
    }

    [Fact]
    public async Task AMissingConnectionIsConnectionUnavailableWithoutALog()
    {
        var unresolved = new Bed(_ => Json(HttpStatusCode.OK, "{}"), resolves: false);
        var unset = new Bed(_ => Json(HttpStatusCode.OK, "{}"));

        var byResolve = await unresolved.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);
        var byNode = await unset.Executor().ExecuteAsync(Context, Node(withoutConnection: true), Variables(), Ct);

        Assert.Equal(("connection_unavailable", false), (byResolve.Outcome, byResolve.Ok));
        Assert.Equal(("connection_unavailable", false), (byNode.Outcome, byNode.Ok));
        Assert.Empty(unresolved.Handler.Requests);
        Assert.Empty(unset.Handler.Requests);
        Assert.Empty(unresolved.Log.Entries);
        Assert.Empty(unset.Log.Entries);
    }

    /// <summary>P2-R11: un cuerpo que no queda en JSON no sale; la fila sí queda.</summary>
    [Fact]
    public async Task ABrokenJsonBodyIsBodyNotJsonWithoutACall()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var node = Node(method: "POST", body: new HttpBody(HttpBodyKinds.Json, """{ "id": {cedula} """, null, null, null));

        var result = await bed.Executor().ExecuteAsync(Context, node, Variables(), Ct);

        Assert.Equal(("body_not_json", "body_not_json", false), (result.Outcome, result.ErrorCode, result.Ok));
        Assert.Empty(bed.Handler.Requests);
        Assert.Equal("body_not_json", Assert.Single(bed.Log.Entries).Outcome);
    }

    [Fact]
    public async Task AJsonBodyIsSentAsApplicationJson()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.Created, """{"id":55}"""));
        var variables = Variables();
        variables.Set("nota", "dijo \"sí\"");
        var node = Node(
            "/service-orders", "POST",
            body: new HttpBody(HttpBodyKinds.Json, """{ "identificationNumber": {cedula}, "note": "{nota}" }""", null, null, null),
            mapping: [new HttpMapping("id", "orden")]);

        var result = await bed.Executor().ExecuteAsync(Context, node, variables, Ct);

        Assert.Equal("55", result.Mapped.TextOf("orden"));
        var request = Assert.Single(bed.Handler.Requests);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{ "identificationNumber": "10203", "note": "dijo \"sí\"" }""", request.Body);
    }

    [Fact]
    public async Task MultipartStreamsTheStoredAttachment()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var messageId = Guid.CreateVersion7();
        bed.Media.Stored[messageId] = new StoredAttachment("tenant/media/f.pdf", "f.pdf", "application/pdf", 3);
        bed.Media.Objects["tenant/media/f.pdf"] = Encoding.ASCII.GetBytes("PDF");
        var variables = Variables();
        variables.Set("factura", new System.Text.Json.Nodes.JsonObject { ["messageId"] = messageId.ToString(), ["fileName"] = "f.pdf" });
        var node = Node(
            "/customers/new-customer", "POST",
            body: new HttpBody(HttpBodyKinds.Multipart, null, [new KeyValue("identificationNumber", "{cedula}")], "file", "factura"));

        var result = await bed.Executor().ExecuteAsync(Context, node, variables, Ct);

        Assert.True(result.Ok);
        var request = Assert.Single(bed.Handler.Requests);
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
        Assert.Equal(
            [("identificationNumber", null, null, "10203"), ("file", "f.pdf", "application/pdf", "PDF")],
            request.Parts.Select(part => (part.Name, part.FileName, part.ContentType, part.Text)));
        Assert.Empty(bed.Delays);
    }

    /// <summary>D-H8 con el desvío del plan: 4 reintentos cada 5 s (20 s, dentro del lease de 30 s de la unidad).</summary>
    [Fact]
    public async Task AnAttachmentNotYetStoredIsMediaNotReady()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var messageId = Guid.CreateVersion7();
        var variables = Variables();
        variables.Set("factura", new System.Text.Json.Nodes.JsonObject { ["messageId"] = messageId.ToString() });
        var node = Node(
            "/customers/new-customer", "POST", body: new HttpBody(HttpBodyKinds.Multipart, null, [], "file", "factura"));

        var result = await bed.Executor().ExecuteAsync(Context, node, variables, Ct);

        Assert.Equal(("media_not_ready", false), (result.Outcome, result.Ok));
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(5), 4), bed.Delays);
        Assert.Equal(5, bed.Media.Lookups);
        Assert.Empty(bed.Handler.Requests);
        Assert.Equal("media_not_ready", Assert.Single(bed.Log.Entries).Outcome);
    }

    [Fact]
    public async Task AnAttachmentStoredDuringTheWaitIsSent()
    {
        var bed = new Bed(_ => Json(HttpStatusCode.OK, "{}"));
        var messageId = Guid.CreateVersion7();
        bed.Media.Objects["k"] = Encoding.ASCII.GetBytes("IMG");
        bed.OnDelay = () =>
        {
            if (bed.Delays.Count == 2)
            {
                bed.Media.Stored[messageId] = new StoredAttachment("k", null, "image/jpeg", 3);
            }
        };
        var variables = Variables();
        variables.Set("factura", new System.Text.Json.Nodes.JsonObject { ["messageId"] = messageId.ToString(), ["fileName"] = null });
        var node = Node("/upload", "POST", body: new HttpBody(HttpBodyKinds.Multipart, null, [], "file", "factura"));

        var result = await bed.Executor().ExecuteAsync(Context, node, variables, Ct);

        Assert.True(result.Ok);
        Assert.Equal(2, bed.Delays.Count);
        Assert.Equal(("file", "IMG"), (Assert.Single(Assert.Single(bed.Handler.Requests).Parts).Name, bed.Handler.Requests[0].Parts[0].Text));
    }

    [Fact]
    public async Task NothingOfTheBodyOrTheKeyIsLogged()
    {
        var logs = new CapturedLogs();
        var ok = new Bed(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") }, logs: logs);
        var failed = new Bed(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<html>secret</html>") }, logs: logs);
        var thrown = new Bed(_ => throw new HttpRequestException("x-api-key " + Key + " <html>"), logs: logs);

        await ok.Executor().ExecuteAsync(Context, Node(mapping: [new HttpMapping("data", "x")]), Variables(), Ct);
        await failed.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);
        await thrown.Executor().ExecuteAsync(Context, Node(), Variables(), Ct);

        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(Key, logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("x-api-key", logs.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10203", logs.AllText, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Bed
    {
        public Bed(
            Func<HttpRequestMessage, HttpResponseMessage> respond, string authHeader = "x-api-key", string? rejection = null, bool resolves = true,
            CapturedLogs? logs = null)
            : this((request, _) => Task.FromResult(respond(request)), authHeader, rejection, resolves, logs)
        {
        }

        public Bed(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, string authHeader = "x-api-key", string? rejection = null,
            bool resolves = true, CapturedLogs? logs = null)
        {
            Handler = new StubHandler(respond);
            Policy = new StubPolicy(rejection);
            Apis = new StubExternalApis(resolves ? new ExternalApiCredentials(new Uri("https://api.clasic.test/v1/"), authHeader, Key) : null);
            LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(logs ?? new CapturedLogs()).SetMinimumLevel(LogLevel.Trace));
        }

        public StubHandler Handler { get; }

        public StubPolicy Policy { get; }

        public StubExternalApis Apis { get; }

        public StubMedia Media { get; } = new();

        public MemoryCallLog Log { get; } = new();

        public List<TimeSpan> Delays { get; } = [];

        public Action? OnDelay { get; set; }

        private ILoggerFactory LoggerFactory { get; }

        public HttpBlockExecutor Executor() =>
            new(
                new StubClientFactory(Handler), Apis, Policy, Media, Log, new FixedClock(Now), LoggerFactory.CreateLogger<HttpBlockExecutor>(),
                (delay, _) =>
                {
                    Delays.Add(delay);
                    OnDelay?.Invoke();
                    return Task.CompletedTask;
                });
    }

    internal sealed record CapturedPart(string? Name, string? FileName, string? ContentType, string Text);

    internal sealed record CapturedRequest(
        string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string? Body, IReadOnlyList<CapturedPart> Parts);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedRequest> requests = new();

        public IReadOnlyList<CapturedRequest> Requests => [.. requests];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            var parts = new List<CapturedPart>();
            string? body = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var disposition = part.Headers.ContentDisposition;
                    parts.Add(new CapturedPart(
                        disposition?.Name?.Trim('"'), disposition?.FileName?.Trim('"'), part.Headers.ContentType?.MediaType,
                        await part.ReadAsStringAsync(cancellationToken)));
                }
            }
            else if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            requests.Enqueue(new CapturedRequest(
                request.Method.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body, parts));
            return await respond(request, cancellationToken);
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(HttpBlockExecutor.HttpClientName, name);
            var client = new HttpClient(handler, disposeHandler: false);
            HttpBlockExecutor.ConfigureClient(client);
            return client;
        }
    }

    internal sealed class StubPolicy(string? rejection) : IExternalApiUrlPolicy
    {
        public const string ConnectRejection = "connect-rejected";

        public List<string> CheckedHeaders { get; } = [];

        public string? Reject(Uri url) => rejection;

        public bool IsReservedHeader(string name)
        {
            CheckedHeaders.Add(name);
            return string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase);
        }

        public string? RejectionIn(Exception exception) =>
            exception.InnerException is InvalidOperationException { Message: ConnectRejection } ? "resolved_to_forbidden_range" : null;
    }

    internal sealed class StubExternalApis(ExternalApiCredentials? credentials) : IAutomationExternalApis
    {
        public Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ExternalApiCredentials?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(tenantId == TenantId && connectionId == ConnectionId ? credentials : null);
    }

    internal sealed class StubMedia : IAutomationMedia
    {
        public ConcurrentDictionary<Guid, StoredAttachment> Stored { get; } = new();

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        public int Lookups { get; private set; }

        public Task<StoredAttachment?> TryGetStoredAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult(tenantId == TenantId && Stored.TryGetValue(messageId, out var stored) ? stored : null);
        }

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(Stream Content, long Length)?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<(Stream Content, long Length)?>(
                Objects.TryGetValue(key, out var bytes) ? (new MemoryStream(bytes), bytes.LongLength) : null);
    }

    internal sealed class MemoryCallLog : IHttpCallLog
    {
        private readonly ConcurrentQueue<HttpCallEntry> entries = new();

        public IReadOnlyList<HttpCallEntry> Entries => [.. entries];

        public Task RecordAsync(HttpCallEntry entry, CancellationToken cancellationToken)
        {
            entries.Enqueue(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    // Un cuerpo sin Content-Length: el tope se tiene que aplicar leyendo, no sólo mirando el header.
    private sealed class UnsizedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }

    /// <summary>Todo lo que se registra, ya formateado y con la excepción entera: lo que guardaría el log JSON de producción. Copia de
    /// <c>UnitTestLogs</c> de Integrations.</summary>
    internal sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> entries = new();

        public IReadOnlyCollection<string> Entries => entries;

        public string AllText => string.Join('\n', entries);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
        }
    }
}
