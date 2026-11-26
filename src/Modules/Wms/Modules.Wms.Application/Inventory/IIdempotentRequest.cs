namespace Modules.Wms.Application;

/// <summary>Marcador de los cuerpos de las rutas IK (clave por header): el filtro de idempotencia hashea el primer argumento que lo
/// implementa. Un cuerpo sin marcador se hashea como <c>null</c>.</summary>
public interface IIdempotentRequest;

/// <summary>Los cuerpos de las rutas SK que llevan la versión del documento: la clave que el servidor deriva la incluye, así que
/// repetir la acción sobre la misma versión repite la respuesta y actuar sobre otra es una operación distinta.</summary>
public interface IVersionedRequest
{
    long Version { get; }
}
