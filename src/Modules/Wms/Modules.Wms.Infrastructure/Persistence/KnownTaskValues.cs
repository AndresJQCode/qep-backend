using Modules.Wms.Domain.Tasks;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// Los valores de enum que este build sabe leer. Un pod más nuevo puede escribir un tipo, un estado, un origen o un estado de línea
/// que este no conoce (el corte T3+ se despliega encima del anterior): el barrido de leases y el tablero filtran por estos valores,
/// porque una fila que no se puede materializar lanza, el barrido deshace todo su lote en cada pasada y el tablero responde 500.
/// <b>No quites el filtro</b>: es lo que deja desplegar el corte siguiente sin migrar en dos pasos. Se guardan como texto y por
/// nombre, así que se comparan por nombre.
/// </summary>
internal static class KnownTaskValues
{
    public static readonly string[] TypeNames = Enum.GetNames<WarehouseTaskType>();

    public static readonly string[] StatusNames = Enum.GetNames<WarehouseTaskStatus>();

    public static readonly string[] LineStatusNames = Enum.GetNames<TaskLineStatus>();

    public static readonly string[] SourceNames = Enum.GetNames<TaskSourceType>();

    public static readonly WarehouseTaskType[] Types = Enum.GetValues<WarehouseTaskType>();

    public static readonly WarehouseTaskStatus[] Statuses = Enum.GetValues<WarehouseTaskStatus>();

    public static readonly TaskLineStatus[] LineStatuses = Enum.GetValues<TaskLineStatus>();

    public static readonly TaskSourceType[] Sources = Enum.GetValues<TaskSourceType>();
}
