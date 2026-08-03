namespace Modules.Storage.Application;

public interface IPublicObjectStorage
{
    bool IsConfigured { get; }

    Task CopyFromPrivateAsync(
        string privateKey,
        string publicKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(string publicKey, CancellationToken cancellationToken);

    string GetUrl(string publicKey);
}
