namespace Modules.Storage.Application;

public interface IFileScanner
{
    Task<FileScanResult> ScanAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

public enum FileScanResult
{
    Clean = 1,
    Infected = 2
}
