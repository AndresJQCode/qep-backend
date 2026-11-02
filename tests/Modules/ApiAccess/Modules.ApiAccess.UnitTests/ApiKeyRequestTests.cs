using Modules.ApiAccess.Domain;

namespace Modules.ApiAccess.UnitTests;

public sealed class ApiKeyRequestTests
{
    private static ApiKeyRequest NewRequest(
        int statusCode = 201,
        string? errorCode = null,
        string path = "/api/v1/external/whatsapp/templates/send",
        string apiKeyName = "ERP",
        string? traceId = "trace-1",
        int durationMs = 12) =>
        ApiKeyRequest.Record(
            Guid.NewGuid(), Guid.NewGuid(), apiKeyName, "POST", path, statusCode,
            errorCode, messageId: null, traceId, durationMs, DateTimeOffset.UtcNow);

    [Fact]
    public void ASuccessKeepsNoErrorCode() =>
        Assert.Null(NewRequest(statusCode: 201, errorCode: "server.unexpected").ErrorCode);

    [Fact]
    public void AFailureKeepsItsErrorCode() =>
        Assert.Equal("server.unexpected", NewRequest(statusCode: 500, errorCode: "server.unexpected").ErrorCode);

    [Fact]
    public void TooLongValuesAreCutToTheirColumns()
    {
        var request = NewRequest(path: "/" + new string('a', 600), apiKeyName: new string('b', 100), traceId: new string('c', 200));
        Assert.Equal((512, 80, 128), (request.Path.Length, request.ApiKeyName.Length, request.TraceId!.Length));
    }

    [Fact]
    public void EachRequestGetsItsOwnTimeOrderedId() =>
        Assert.Equal(7, NewRequest().Id.Version);

    [Fact]
    public void ANegativeDurationIsZero() =>
        Assert.Equal(0, NewRequest(durationMs: -5).DurationMs);
}
