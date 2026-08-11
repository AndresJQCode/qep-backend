using Modules.Storage.Application;

namespace Modules.Storage.Infrastructure.Scanning;

// Fallback explícito para desarrollo local. Producción habilita Storage:ClamAv:Enabled.
internal sealed class NoopFileScanner : IFileScanner
{
    public Task<FileScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        Task.FromResult(FileScanResult.Clean);
}
