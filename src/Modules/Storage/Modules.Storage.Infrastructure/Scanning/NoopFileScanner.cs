using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Scanning;

// Explicit local-development fallback. Production enables Storage:ClamAv:Enabled.
internal sealed class NoopFileScanner : IFileScanner
{
    public Task<FileScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        Task.FromResult(FileScanResult.Clean);
}
