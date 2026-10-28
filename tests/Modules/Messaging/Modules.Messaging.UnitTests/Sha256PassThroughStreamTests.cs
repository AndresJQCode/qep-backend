using System.Security.Cryptography;
using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>§8.6, paso 3: el hash se calcula al pasar, sin segunda lectura.</summary>
public sealed class Sha256PassThroughStreamTests
{
    [Fact]
    public async Task TheHashMatchesTheBytesThatWentThrough()
    {
        var bytes = new byte[100_000];
        Random.Shared.NextBytes(bytes);
        await using var inner = new MemoryStream(bytes);
        await using var stream = new Sha256PassThroughStream(inner);
        await using var sink = new MemoryStream();

        await stream.CopyToAsync(sink, 4096, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, sink.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), stream.FinishHex());
        Assert.Equal(bytes.Length, stream.BytesRead);
    }
}
