using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Scanning;

internal sealed class ClamAvFileScanner(IOptions<StorageOptions> options) : IFileScanner
{
    private const int ChunkSize = 64 * 1024;

    public async Task<FileScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.ClamAv;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);

        var offset = 0;
        var lengthPrefix = new byte[sizeof(int)];
        while (offset < content.Length)
        {
            var length = Math.Min(ChunkSize, content.Length - offset);
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, length);
            await stream.WriteAsync(lengthPrefix, timeout.Token);
            await stream.WriteAsync(content.Slice(offset, length), timeout.Token);
            offset += length;
        }

        Array.Clear(lengthPrefix);
        await stream.WriteAsync(lengthPrefix, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        var responseBuffer = new byte[4096];
        var count = await stream.ReadAsync(responseBuffer, timeout.Token);
        var response = Encoding.UTF8.GetString(responseBuffer, 0, count).TrimEnd('\0', '\r', '\n');
        if (response.EndsWith("OK", StringComparison.Ordinal))
        {
            return FileScanResult.Clean;
        }

        if (response.Contains("FOUND", StringComparison.Ordinal))
        {
            return FileScanResult.Infected;
        }

        throw new InvalidOperationException($"ClamAV returned an unexpected response: {response}");
    }
}
