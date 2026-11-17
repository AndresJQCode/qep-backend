namespace Modules.Support.Application;

/// <summary>
/// Fase 4b (H8): el PDF de la respuesta formal en el bucket privado, con adaptador en Bootstrapper sobre Storage. Va bajo el prefijo
/// <c>support/</c>, que no tiene regla de ciclo de vida: es prueba legal y se guarda 10 años (spec §13.10). Nunca bajo
/// <c>quotations/</c>, que sí la tiene.
/// </summary>
public interface ISupportDocumentStorage
{
    /// <summary>Guarda el PDF con una clave nueva (<c>support/tenants/{tenantId:N}/replies/{yyyy}/{MM}/{guid:N}.pdf</c>) y la devuelve.</summary>
    Task<string> SaveAsync(Guid tenantId, byte[] content, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken);

    /// <summary>Una URL firmada de descarga con el nombre de archivo dado: 5 minutos en la consola, 10 en la consulta pública.</summary>
    Task<Uri> CreateDownloadUrlAsync(string storageKey, string fileName, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Best effort, nunca lanza: limpia el objeto de un documento que no llegó a guardarse (H7). Si falla, queda un objeto
    /// huérfano y un log.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
