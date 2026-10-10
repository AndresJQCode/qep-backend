using Api;
using Microsoft.AspNetCore.Http;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §11, «firma antes de la base»: un POST anónimo rechazado al webhook no escribe
/// fila en <c>platform.request_failures</c>; el resto de las escrituras sí.</summary>
public sealed class RequestFailureCaptureTests
{
    private static bool ShouldCapture(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return RequestFailureCapture.ShouldCapture(context);
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
}
