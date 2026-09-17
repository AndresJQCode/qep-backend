namespace Modules.Storage.Application;

public interface IPublicObjectStorage
{
    bool IsConfigured { get; }

    Task CopyFromPrivateAsync(
        string privateKey,
        string publicKey,
        CancellationToken cancellationToken);

    Task DeleteAsync(string publicKey, CancellationToken cancellationToken);

    /// <summary>
    /// Si el objeto existe en el bucket público. <c>PaymentProofMoveProcessor</c> lo pregunta antes de
    /// borrar el temporal de un comprobante: un pedido guardado cuyo request igual falló (la conexión se
    /// cortó esperando el COMMIT) ya borró su copia en el rollback, y borrar el temporal dejaría el
    /// comprobante sin ninguna copia (revisión final, I1). Un error de R2 se propaga: no saber no es
    /// «no existe».
    /// </summary>
    Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken);

    string GetUrl(string publicKey);

    /// <summary>
    /// Una página de los objetos del bucket público bajo <paramref name="prefix"/>, con su fecha de
    /// modificación (spec 2026-09-16, D12). <paramref name="continuationToken"/> es el de la página
    /// anterior, o null para la primera; la última página trae null.
    /// </summary>
    Task<PublicObjectPage> ListAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken);
}

public sealed record PublicObjectPage(
    IReadOnlyList<PublicStoredObject> Objects,
    string? ContinuationToken);

public sealed record PublicStoredObject(string Key, DateTimeOffset LastModified);
