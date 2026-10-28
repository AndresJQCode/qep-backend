using System.Text;
using Microsoft.AspNetCore.Http;
using Modules.Messaging.Api;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2: el cuerpo se lee una vez, con tope; lo que Kestrel corta (cuerpo truncado,
/// tope superado en chunked) vuelve con su status y no como 500. TestServer no valida Content-Length, así que
/// el corte se simula con un stream que tira lo mismo que Kestrel.</summary>
public sealed class WebhookBodyReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ThrowingStream(int statusCode) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("cut", statusCode);
    }

    [Fact]
    public async Task ABodyWithinTheCapIsReturnedWhole()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":1}");

        var result = await WebhookBodyReader.ReadAsync(new MemoryStream(bytes), bytes.Length, 1024, Ct);

        Assert.Equal(bytes, result.Body);
        Assert.Equal(StatusCodes.Status200OK, result.Status);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheCapIs413WithoutReading()
    {
        var result = await WebhookBodyReader.ReadAsync(new ThrowingStream(StatusCodes.Status400BadRequest), 2048, 1024, Ct);

        Assert.Null(result.Body);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.Status);
    }

    [Fact]
    public async Task AStreamOverTheCapWithoutDeclaredLengthIs413()
    {
        var result = await WebhookBodyReader.ReadAsync(new MemoryStream(new byte[2048]), null, 1024, Ct);

        Assert.Null(result.Body);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.Status);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    public async Task WhatKestrelCutsComesBackWithItsStatus(int statusCode)
    {
        var result = await WebhookBodyReader.ReadAsync(new ThrowingStream(statusCode), null, 1024, Ct);

        Assert.Null(result.Body);
        Assert.Equal(statusCode, result.Status);
    }
}
