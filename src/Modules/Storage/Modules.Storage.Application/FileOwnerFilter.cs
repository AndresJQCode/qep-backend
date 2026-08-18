using Modules.Storage.Domain;

namespace Modules.Storage.Application;

/// <summary>
/// El filtro por dueño del listado de archivos (CAT-09): a qué entidad pertenecen los archivos
/// que se piden.
///
/// **Los dos campos van juntos o no va ninguno.** Un <c>ownerId</c> sin tipo es un <c>Guid</c>
/// que podría ser un producto, un usuario o una entidad —los tres se guardan en la misma
/// columna—, así que devolver la unión de los tres sería un resultado que nadie pidió.
///
/// **Y un tipo inválido falla en vez de ignorarse.** El filtro de <c>status</c> del mismo
/// listado hace lo contrario: si el string no parsea cae en <c>null</c> y devuelve la lista
/// **sin filtrar**, como si no se hubiera pedido nada. Es el mismo error que CAT-05 corrigió en
/// el <c>POST</c>, donde un <c>ownerType</c> inválido se convertía en <c>User</c> y respondía
/// 201. Un error es mejor que un dato falso, y una lista sin filtrar cuando pediste filtrarla
/// es un dato falso.
/// </summary>
public sealed record FileOwnerFilter(Guid OwnerId, FileOwnerType OwnerType)
{
    /// <summary>
    /// Devuelve el filtro, o <c>null</c> cuando no se pidió ninguno. Lanza si la petición está
    /// a medio armar o el tipo no existe.
    /// </summary>
    public static FileOwnerFilter? Resolve(Guid? ownerId, string? ownerType)
    {
        var hasId = ownerId.HasValue;
        var hasType = !string.IsNullOrWhiteSpace(ownerType);

        if (!hasId && !hasType)
        {
            return null;
        }

        // Primero la forma de la petición y después el valor: si falta uno de los dos campos, el
        // problema es que el filtro está a medias, y decir "el tipo es inválido" mandaría a
        // corregir lo que no está mal.
        if (hasId != hasType)
        {
            throw new StorageDomainException(
                "storage.file.owner_filter_incomplete",
                "The owner filter needs both ownerId and ownerType, or neither.");
        }

        return new FileOwnerFilter(ownerId!.Value, ParseOwnerType(ownerType!));
    }

    // Mismo descarte que el POST desde CAT-05: Enum.TryParse acepta el número crudo ("4"), que
    // no es contrato, así que se exige que el valor esté definido y no empiece con un dígito.
    private static FileOwnerType ParseOwnerType(string ownerType)
    {
        var trimmed = ownerType.Trim();
        return !Enum.TryParse<FileOwnerType>(trimmed, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed) ||
            char.IsDigit(trimmed.FirstOrDefault())
            ? throw new StorageDomainException(
                "storage.file.owner_type_invalid",
                "The owner type is not one of the supported values.")
            : parsed;
    }
}
