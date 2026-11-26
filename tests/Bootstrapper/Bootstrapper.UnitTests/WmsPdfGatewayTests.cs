using System.Net;
using Microsoft.Extensions.Options;
using Modules.Quotations.Infrastructure;
using Modules.Wms.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// La salida de Bodegas hacia <c>qcode-pdf</c>. Lo que se prueba es la promesa de <c>IWmsPdfGateway</c>: cualquier falla del
/// servicio llega como 422 <c>wms.label.render_failed</c> y no como un 500 sin explicación.
/// </summary>
public sealed class WmsPdfGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private static WmsPdfGateway GatewayFor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(
            new HttpClient(new FakeHandler(respond)),
            Options.Create(new QuotationsOptions { Pdf = new PdfOptions { BaseUrl = "https://pdf.test", ApiKey = "key" } }));

    private static Task<byte[]> RenderAsync(WmsPdfGateway gateway, CancellationToken ct) =>
        gateway.RenderAsync("#let x = 1", new { labels = Array.Empty<string>() }, "etiquetas.pdf", ct);

    [Fact]
    public async Task ANetworkFailureIsARenderFailedDomainError()
    {
        var gateway = GatewayFor((_, _) => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<WmsDomainException>(() => RenderAsync(gateway, Ct));

        Assert.Equal(WmsErrorCodes.LabelRenderFailed, error.Code);
    }

    [Fact]
    public async Task ATimeoutIsARenderFailedDomainError()
    {
        // HttpClient reporta su Timeout como TaskCanceledException aunque el token del llamador siga vivo.
        var gateway = GatewayFor((_, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var error = await Assert.ThrowsAsync<WmsDomainException>(() => RenderAsync(gateway, Ct));

        Assert.Equal(WmsErrorCodes.LabelRenderFailed, error.Code);
    }

    [Fact]
    public async Task ACancellationRequestedByTheCallerIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gateway = GatewayFor((_, token) => Task.FromCanceled<HttpResponseMessage>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RenderAsync(gateway, cts.Token));
    }

    [Fact]
    public async Task ANonSuccessStatusIsARenderFailedDomainError()
    {
        var gateway = GatewayFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("typst error") }));

        var error = await Assert.ThrowsAsync<WmsDomainException>(() => RenderAsync(gateway, Ct));

        Assert.Equal(WmsErrorCodes.LabelRenderFailed, error.Code);
        Assert.Contains("typst error", error.Message);
    }

    [Fact]
    public async Task ASuccessfulResponseReturnsItsBytes()
    {
        var gateway = GatewayFor((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));

        Assert.Equal(new byte[] { 1, 2, 3 }, await RenderAsync(gateway, Ct));
    }
}
