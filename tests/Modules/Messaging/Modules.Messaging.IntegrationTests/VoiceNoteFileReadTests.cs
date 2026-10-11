using Microsoft.AspNetCore.Http;
using Modules.Messaging.Api;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §6.1: el endpoint lleva la nota de voz a memoria una sola vez. Copiarla a un
/// <c>MemoryStream</c> y después a un arreglo duplicaba hasta 16 MB en el LOH por request.</summary>
public sealed class VoiceNoteFileReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FormFile FileOf(byte[] bytes) => new(new MemoryStream(bytes, writable: false), 0, bytes.Length, "file", "voice.ogg");

    [Fact]
    public async Task TheAudioIsReadIntoASingleArrayOfItsSize()
    {
        const int size = 8 * 1024 * 1024;
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        var file = FileOf(bytes);
        // Calienta el camino (JIT, buffers del pool) para que la medición vea sólo la lectura.
        await MessagingEndpoints.ReadAudioAsync(FileOf([1, 2, 3]), Ct);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var audio = await MessagingEndpoints.ReadAudioAsync(file, Ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(bytes, audio);
        Assert.InRange(allocated, size, size + (256 * 1024));
    }

    [Fact]
    public async Task WithoutFileThereIsNoAudio() =>
        Assert.Null(await MessagingEndpoints.ReadAudioAsync(null, Ct));
}
