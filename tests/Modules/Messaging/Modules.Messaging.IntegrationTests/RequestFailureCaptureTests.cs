using Api;
using Microsoft.AspNetCore.Http;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §11, «firma antes de la base»: un POST anónimo rechazado al webhook no escribe
/// fila en <c>platform.request_failures</c>; el resto de las escrituras sí.</summary>
public sealed class RequestFailureCaptureTests
{
    private static bool ShouldCapture(string method, string path, int statusCode = StatusCodes.Status422UnprocessableEntity)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return RequestFailureCapture.ShouldCapture(context, statusCode);
    }

    [Theory]
    [InlineData("/api/webhooks/whatsapp")]
    [InlineData("/api/webhooks/other")]
    public void TheWebhookPrefixIsNotCaptured(string path) =>
        Assert.False(ShouldCapture("POST", path));

    [Theory]
    [InlineData("/api/v1/tenants/x/messaging/conversations")]
    [InlineData("/api/webhooksx")]
    [InlineData("/API/webhooks/whatsapp")]
    public void OtherWritesAreStillCaptured(string path) =>
        Assert.True(ShouldCapture("POST", path));

    [Fact]
    public void ReadsAreNotCaptured() =>
        Assert.False(ShouldCapture("GET", "/api/v1/tenants/x/messaging/conversations"));

    // Spec 2026-10-30 §6.4: en la API externa el 401 (clave inventada) y el 429 (límite) son ruido anónimo que no debe
    // escribir filas; el 403, el 422 y el 502 son fallas de una clave legítima y sí.
    [Theory]
    [InlineData(401, false)]
    [InlineData(429, false)]
    [InlineData(403, true)]
    [InlineData(422, true)]
    [InlineData(502, true)]
    public void TheExternalApiOnlySkipsAnonymousNoise(int status, bool expected) =>
        Assert.Equal(expected, ShouldCapture("POST", "/api/v1/external/whatsapp/messages", status));

    [Fact]
    public void AnInternal401IsStillCaptured() =>
        Assert.True(ShouldCapture("POST", "/api/v1/tenants/x/settings", 401));

    [Fact]
    public void TheExternalPrefixIsOrdinalAndNeedsItsSlash() =>
        Assert.True(ShouldCapture("POST", "/api/v1/externalx", 401));
}
