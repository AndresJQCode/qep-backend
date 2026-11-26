namespace Modules.Store.Application;

public interface IStoreFileLookup
{
    /// <summary>
    /// La URL pública de un archivo que sirve de banner, o null si no lo es: no es del tenant, su dueño no es <c>Store</c>, no es una
    /// imagen, no está disponible o no está publicado. Un solo null para todas, igual que el logo: no se distingue «no existe» de «no es tuyo».
    /// </summary>
    Task<string?> FindPublicImageAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);
}
