using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Coordinadora.Application;
using Modules.Coordinadora.Domain;
using Modules.Coordinadora.Infrastructure.Api;

namespace Modules.Coordinadora.UnitTests;

/// <summary>
/// Spec 2026-11-21 §3: el cliente de Coordinadora contra un handler falso; ninguna prueba llama a Coordinadora. El AGS por JSON-RPC con las
/// credenciales dentro de <c>p</c>; las guías por SOAP 1.2 con <c>id_cliente</c>, <c>usuario</c> y la clave en SHA-256; los <c>Fault</c> son
/// rechazos; 5xx sin Fault, timeout y red, transitorios; el PDF se lee de cualquier texto en base64 (V-2).
/// </summary>
public sealed class CoordinadoraApiClientTests : IDisposable
{
    private const string ApiKey = "coordinadora-apikey-SENTINEL";
    private const string ApiPassword = "coordinadora-clave-SENTINEL";
    private const string GuidesPassword = "secreto-guias";

    private static readonly CoordinadoraCredentials Credentials = new(
        Guid.Parse("0193c0de-0009-7000-8000-000000000009"), "800123456", ApiKey, ApiPassword, "777", "usuario.guias", GuidesPassword);

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 fake");
    private static readonly string PdfBase64 = Convert.ToBase64String(PdfBytes);

    private readonly CoordinadoraHttpStub _api = new();
    private readonly CoordinadoraApiClient _client;

    public CoordinadoraApiClientTests()
    {
        _client = new CoordinadoraApiClient(
            new HttpClient(_api, disposeHandler: false),
            Options.Create(new CoordinadoraOptions
            {
                AgsUrl = "https://ags.test/ags/1.5/server.php",
                GuidesUrl = "https://guias.test/ws/guias/1.6/server.php",
            }));
    }

    public void Dispose() => _api.Dispose();

    private static string Soap(string inner) =>
        $"""<?xml version="1.0"?><env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope"><env:Body>{inner}</env:Body></env:Envelope>""";

    private static string Fault(string code, string reason) =>
        Soap($"""<env:Fault><env:Code><env:Value>{code}</env:Value></env:Code><env:Reason><env:Text xml:lang="es">{reason}</env:Text></env:Reason></env:Fault>""");

    // --- Cotizador ---

    [Fact]
    public async Task TheQuoteGoesByJsonRpcWithTheCredentialsInsideP()
    {
        _api.On("/ags/1.5/server.php", HttpStatusCode.OK, """{"jsonrpc":"2.0","result":{"flete_fijo":"8000","flete_variable":"2500.50","flete_total":"10500.50"},"id":1}""");

        var outcome = await _client.QuoteAsync(Credentials, new CoordinadoraQuoteRequest("""{"origen":"05001000","destino":"11001000","valoracion":"90000"}"""), TestContext.Current.CancellationToken);

        var quoted = Assert.IsType<CoordinadoraQuoteOutcome.Quoted>(outcome);
        Assert.Equal(10500.50m, quoted.FreightTotal);
        Assert.Contains("\"flete_fijo\"", quoted.ResultJson, StringComparison.Ordinal);
        var request = Assert.Single(_api.Requests);
        Assert.Equal("https://ags.test/ags/1.5/server.php", request.Uri);
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Cotizador.cotizar", body.RootElement.GetProperty("method").GetString());
        var p = body.RootElement.GetProperty("params").GetProperty("p");
        Assert.Equal(("800123456", ApiKey, ApiPassword), (p.GetProperty("nit").GetString(), p.GetProperty("apikey").GetString(), p.GetProperty("clave").GetString()));
        Assert.Equal("05001000", p.GetProperty("origen").GetString());
    }

    [Fact]
    public async Task AJsonRpcErrorIsARejection()
    {
        _api.On("/ags/1.5/server.php", HttpStatusCode.OK, """{"jsonrpc":"2.0","error":{"code":-32000,"message":"apikey invalida"},"id":1}""");

        var outcome = await _client.QuoteAsync(Credentials, new CoordinadoraQuoteRequest("{}"), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<CoordinadoraQuoteOutcome.Rejected>(outcome);
        Assert.Equal(("-32000", "apikey invalida"), (rejected.Code, rejected.Message));
    }

    // Un error sin message no se vuelca: su data podría ecoar los parámetros, apikey y clave incluidas, y el mensaje llega al 422.
    [Fact]
    public async Task AJsonRpcErrorWithoutMessageNeverEchoesTheRequest()
    {
        var body = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":13,\"data\":{\"p\":{\"apikey\":\"" + ApiKey + "\",\"clave\":\"" + ApiPassword + "\"}}},\"id\":1}";
        _api.On("/ags/1.5/server.php", HttpStatusCode.OK, body);

        var outcome = await _client.QuoteAsync(Credentials, new CoordinadoraQuoteRequest("{}"), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<CoordinadoraQuoteOutcome.Rejected>(outcome);
        Assert.Equal(("13", "error"), (rejected.Code, rejected.Message));
        Assert.DoesNotContain(ApiKey, rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultThatCarriesAnErrorIsARejectionToo()
    {
        _api.On("/ags/1.5/server.php", HttpStatusCode.OK, """{"jsonrpc":"2.0","result":{"error":{"code":"13","message":"Ciudad destino no existe"}},"id":1}""");

        var outcome = await _client.QuoteAsync(Credentials, new CoordinadoraQuoteRequest("{}"), TestContext.Current.CancellationToken);

        Assert.Equal("13", Assert.IsType<CoordinadoraQuoteOutcome.Rejected>(outcome).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http_500")]
    [InlineData(HttpStatusCode.Found, "http_302")]
    public async Task AServerErrorOrARedirectOnTheQuoteIsTransient(HttpStatusCode status, string reason)
    {
        _api.On("/ags/1.5/server.php", status, "<html>down</html>");

        var outcome = await _client.QuoteAsync(Credentials, new CoordinadoraQuoteRequest("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(reason, Assert.IsType<CoordinadoraQuoteOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public async Task A200WithoutResultIsTransientNotQuoted()
    {
        _api.On("/ags/1.5/server.php", HttpStatusCode.OK, "<html>maintenance</html>");

        var outcome = await _client.QuoteAsync(Credentials, new CoordinadoraQuoteRequest("{}"), TestContext.Current.CancellationToken);

        Assert.Equal("invalid_response", Assert.IsType<CoordinadoraQuoteOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public void TheFreightTotalIsReadFromAnObjectOrTheFirstElementOfAnArray()
    {
        Assert.Equal(1500m, CoordinadoraApiClient.FreightTotalOf(JsonDocument.Parse("""[{"total":1500}]""").RootElement));
        Assert.Equal(1500.5m, CoordinadoraApiClient.FreightTotalOf(JsonDocument.Parse("""{"flete_total":"1500.50"}""").RootElement));
        Assert.Null(CoordinadoraApiClient.FreightTotalOf(JsonDocument.Parse("""{"otro":1}""").RootElement));
    }

    // --- Guías ---

    [Fact]
    public async Task TheGuideGoesBySoap12WithTheHashedPasswordAndTheItemsAsElements()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap($"""<ns:Guias_generarGuiaResponse xmlns:ns="https://guias.test/ws/guias/1.6/server.php"><return><codigo_remision>12345678901</codigo_remision><pdf>{PdfBase64}</pdf></return></ns:Guias_generarGuiaResponse>"""));
        var request = new CoordinadoraGuideRequest("""{"codigo_remision":"","nombre_destinatario":"Pedro Pérez","valor_declarado":"90000","detalle":[{"ubl":"0","peso":"1.5"}],"notificaciones":{},"atributos_retorno":{"nit":""}}""");

        var outcome = await _client.CreateGuideAsync(Credentials, request, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CoordinadoraGuideOutcome.Created>(outcome);
        Assert.Equal("12345678901", created.GuideNumber);
        Assert.Equal(PdfBytes, created.LabelPdf);
        var sent = Assert.Single(_api.Requests);
        Assert.Equal("https://guias.test/ws/guias/1.6/server.php", sent.Uri);
        Assert.StartsWith("application/soap+xml", sent.ContentType, StringComparison.Ordinal);
        Assert.Contains("action=\"https://guias.test/ws/guias/1.6/server.php#Guias_generarGuia\"", sent.ContentType, StringComparison.Ordinal);
        var envelope = XDocument.Parse(sent.Body!);
        XNamespace env = "http://www.w3.org/2003/05/soap-envelope";
        XNamespace ns = "https://guias.test/ws/guias/1.6/server.php";
        var operation = Assert.Single(envelope.Descendants(ns + "Guias_generarGuia"));
        var p = Assert.Single(operation.Elements("p"));
        Assert.Equal("Pedro Pérez", p.Element("nombre_destinatario")!.Value);
        Assert.Equal("1.5", p.Element("detalle")!.Element("item")!.Element("peso")!.Value);
        Assert.Empty(p.Element("notificaciones")!.Elements());
        Assert.Equal("", p.Element("atributos_retorno")!.Element("nit")!.Value);
        Assert.Equal(("777", "usuario.guias"), (p.Element("id_cliente")!.Value, p.Element("usuario")!.Value));
        Assert.Equal(CoordinadoraApiClient.GuidesPasswordHash(GuidesPassword), p.Element("clave")!.Value);
        Assert.DoesNotContain(GuidesPassword, sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, sent.Body, StringComparison.Ordinal);
        Assert.NotNull(envelope.Descendants(env + "Body").SingleOrDefault());
    }

    [Fact]
    public void ThePasswordHashIsSha256InLowercaseHex()
    {
        Assert.Equal("2bb80d537b1da3e38bd30361aa855686bde0eacd7162fef6a25fe97bf527a25b", CoordinadoraApiClient.GuidesPasswordHash("secret"));
        Assert.Equal(64, CoordinadoraApiClient.GuidesPasswordHash(GuidesPassword).Length);
    }

    [Theory]
    [InlineData("env:Sender")]
    [InlineData("SOAP-ENV:Client")]
    public async Task AClientFaultIsARejectionEvenWithHttp500(string code)
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.InternalServerError, Fault(code, "Ciudad de destino no valida: 11001000"));

        var outcome = await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<CoordinadoraGuideOutcome.Rejected>(outcome);
        Assert.Equal((code, "Ciudad de destino no valida: 11001000"), (rejected.Code, rejected.Message));
    }

    // Un Fault del servidor pudo llegar después de crear la guía: sin clave de idempotencia, liberar el pedido generaría dos.
    [Theory]
    [InlineData("env:Receiver", "fault_receiver")]
    [InlineData("SOAP-ENV:Server", "fault_server")]
    [InlineData("ERR-500", "fault_err-500")]
    public async Task AServerFaultOnCreateIsTransientNotARejection(string code, string reason)
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.InternalServerError, Fault(code, "Error interno"));

        var outcome = await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken);

        Assert.Equal(reason, Assert.IsType<CoordinadoraGuideOutcome.Transient>(outcome).Reason);
    }

    [Fact]
    public async Task AServerErrorWithoutFaultATimeoutOrTheNetworkIsTransientAndNothingIsResent()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        Assert.Equal("http_502", Assert.IsType<CoordinadoraGuideOutcome.Transient>(
            await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken)).Reason);

        _api.Throw("/ws/guias/1.6/server.php", new TaskCanceledException("timed out", new TimeoutException()));
        Assert.Equal("timeout", Assert.IsType<CoordinadoraGuideOutcome.Transient>(
            await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken)).Reason);

        _api.Throw("/ws/guias/1.6/server.php", new HttpRequestException("refused"));
        Assert.Equal("network", Assert.IsType<CoordinadoraGuideOutcome.Transient>(
            await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken)).Reason);

        Assert.Equal(3, _api.Requests.Count);
    }

    [Fact]
    public async Task A200WithoutGuideNumberIsTransientUnlessItNamesAnError()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap("<return><otra_cosa>1</otra_cosa></return>"));
        Assert.Equal("invalid_response", Assert.IsType<CoordinadoraGuideOutcome.Transient>(
            await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken)).Reason);

        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap("<return><error>Cuenta sin saldo</error></return>"));
        var rejected = Assert.IsType<CoordinadoraGuideOutcome.Rejected>(
            await _client.CreateGuideAsync(Credentials, new CoordinadoraGuideRequest("{}"), TestContext.Current.CancellationToken));
        Assert.Equal("Cuenta sin saldo", rejected.Message);
    }

    [Fact]
    public async Task TheLabelIsTheFirstPdfInTheAnswerAndWithoutOneItIsUnavailable()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap($"<return><rotulo>{PdfBase64}</rotulo></return>"));
        Assert.Equal(PdfBytes, await _client.PrintLabelAsync(Credentials, "12345678901", TestContext.Current.CancellationToken));
        var sent = Assert.Single(_api.Requests);
        Assert.Contains("<codigo_remision>12345678901</codigo_remision>", sent.Body, StringComparison.Ordinal);
        Assert.Contains("Guias_imprimirRotulos", sent.Body, StringComparison.Ordinal);

        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap("<return><nada>x</nada></return>"));
        var unavailable = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => _client.PrintLabelAsync(Credentials, "12345678901", TestContext.Current.CancellationToken));
        Assert.Equal(CoordinadoraErrorCodes.Unavailable, unavailable.Code);
    }

    [Fact]
    public async Task AFaultOnAReadIsCarrierRejectedAndATransientIsUnavailable()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.InternalServerError, Fault("env:Receiver", "Guia no existe"));
        var rejected = await Assert.ThrowsAsync<CoordinadoraDomainException>(
            () => _client.TrackAsync(Credentials, "1", TestContext.Current.CancellationToken));
        Assert.Equal(CoordinadoraErrorCodes.CarrierRejected, rejected.Code);
        Assert.Contains("Guia no existe", rejected.Message, StringComparison.Ordinal);

        _api.Throw("/ws/guias/1.6/server.php", new HttpRequestException("refused"));
        Assert.Equal(
            CoordinadoraErrorCodes.Unavailable,
            (await Assert.ThrowsAsync<ServiceUnavailableException>(() => _client.TrackAsync(Credentials, "1", TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task TrackingReadsTheKnownFieldsOrFlattensTheBody()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap("<return><estado>EN REPARTO</estado><descripcion>Salio a reparto</descripcion></return>"));
        var known = await _client.TrackAsync(Credentials, "1", TestContext.Current.CancellationToken);
        Assert.Equal(new CoordinadoraTracking("EN REPARTO", "Salio a reparto"), known);

        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap("<return><situacion>ENTREGADO</situacion><fecha>2026-11-22</fecha><nombre_destinatario>Pedro Pérez</nombre_destinatario><telefono>3189023450</telefono></return>"));
        var unknown = await _client.TrackAsync(Credentials, "1", TestContext.Current.CancellationToken);
        Assert.Null(unknown.Status);
        // Lo que nombra al destinatario sale sin valor: tracking_detail vuelve en el GET.
        Assert.Equal("situacion: ENTREGADO | fecha: 2026-11-22 | nombre_destinatario: … | telefono: …", unknown.Detail);
        Assert.DoesNotContain("Pedro", unknown.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingIsAcceptedWithoutFaultAndRejectedWithOne()
    {
        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Soap("<return><mensaje>Guia anulada</mensaje></return>"));
        Assert.IsType<CoordinadoraCancelOutcome.Cancelled>(await _client.CancelGuideAsync(Credentials, "1", TestContext.Current.CancellationToken));
        Assert.Contains("Guias_anularGuia", Assert.Single(_api.Requests).Body, StringComparison.Ordinal);

        _api.On("/ws/guias/1.6/server.php", HttpStatusCode.OK, Fault("env:Sender", "La guia ya fue despachada"));
        var rejected = Assert.IsType<CoordinadoraCancelOutcome.Rejected>(await _client.CancelGuideAsync(Credentials, "1", TestContext.Current.CancellationToken));
        Assert.Equal("La guia ya fue despachada", rejected.Message);
    }

    [Fact]
    public void TheCredentialsNeverPrintTheirSecrets()
    {
        var text = Credentials.ToString();

        Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(GuidesPassword, text, StringComparison.Ordinal);
        Assert.Contains(Credentials.ConnectionId.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNamespaceDefaultsToTheGuidesUrlAndCanBeOverridden()
    {
        Assert.Equal("https://guias.coordinadora.com/ws/guias/1.6/server.php", new CoordinadoraOptions().EffectiveGuidesNamespace());
        Assert.Equal("urn:guias", new CoordinadoraOptions { GuidesNamespace = " urn:guias " }.EffectiveGuidesNamespace());
    }
}

internal sealed class CoordinadoraHttpStub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<CoordinadoraStubRequest> _requests = new();

    public IReadOnlyCollection<CoordinadoraStubRequest> Requests => _requests;

    public void On(string path, HttpStatusCode status, string body) =>
        _routes[path] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    public void Throw(string path, Exception exception) => _routes[path] = () => throw exception;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        _requests.Enqueue(new CoordinadoraStubRequest(request.RequestUri.ToString(), request.Content?.Headers.ContentType?.ToString() ?? string.Empty, body));
        return _routes.TryGetValue(path, out var answer) ? answer() : throw new InvalidOperationException($"No stub for {request.Method} {path}.");
    }
}

internal sealed record CoordinadoraStubRequest(string Uri, string ContentType, string? Body);
