using BuildingBlocks.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Application;

/// <summary>
/// El chequeo de todo handler de la consola de Storage que carga un archivo por id (fix round 1 del Bloque D): un archivo de dueño de
/// sistema (<see cref="SystemOwnedFiles"/>) responde el mismo 404 <c>storage.file.not_found</c> que un id de otro tenant, así no
/// confirma que existe. Va junto al 404 de tenant, antes del guard de módulo y de cualquier efecto. La subida sin miembro
/// (<see cref="StorageSystemUploads"/>) no pasa por acá: es el único camino a esos archivos.
/// </summary>
internal static class SystemOwnedFileGuard
{
    public static void EnsureVisibleToMembers(FileResource resource)
    {
        if (SystemOwnedFiles.Includes(resource.OwnerType))
        {
            throw new ResourceNotFoundException("storage.file.not_found", "The file resource was not found.");
        }
    }
}
