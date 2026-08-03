namespace Modules.Storage.Application;

public interface IImageVariantGenerator
{
    bool Supports(string mimeType);

    Task<IReadOnlyList<GeneratedFileVariant>> GenerateAsync(
        byte[] content,
        CancellationToken cancellationToken);
}

public sealed record GeneratedFileVariant(
    string Name,
    byte[] Content,
    string MimeType,
    string Extension,
    int Width,
    int Height);
