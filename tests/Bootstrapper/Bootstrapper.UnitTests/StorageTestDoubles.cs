using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de los puertos de Storage que usan los adaptadores del composition root. A mano y sin
// librería de mocking, como el resto del repositorio: anotan lo que reciben.

/// <summary>Los archivos que siembra la prueba, por id. Sólo <see cref="GetAsync"/>: es lo único que
/// el publicador de comprobantes lee.</summary>
internal sealed class InMemoryFileResourceRepository(params FileResource[] resources)
    : IFileResourceRepository
{
    public void Add(FileResource resource) => throw new NotSupportedException();

    public Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken) =>
        Task.FromResult(resources.FirstOrDefault(resource => resource.Id == id));

    public Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        FileOwnerFilter? owner,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El bucket público: anota cada copia (clave pública → clave privada de origen) y cada
/// borrado. <see cref="GetUrl"/> arma la URL con <see cref="BaseUrl"/>, como R2PublicObjectStorage con
/// Storage:R2:PublicBaseUrl.</summary>
internal sealed class RecordingPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets-qep.example.co";

    public Dictionary<string, string> Copies { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(
        string privateKey, string publicKey, CancellationToken cancellationToken)
    {
        Copies[publicKey] = privateKey;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    // El publicador tampoco pregunta si una copia existe: eso es del movimiento de Storage.
    public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    // El publicador de comprobantes no lista el bucket: eso es de la reconciliación de Storage.
    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
