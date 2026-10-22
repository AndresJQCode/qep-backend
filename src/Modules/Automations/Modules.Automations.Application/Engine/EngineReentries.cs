namespace Modules.Automations.Application.Engine;

/// <summary>
/// Ruling P2-R40: el motor corta un ciclo dentro de un recorrido (P2-R13), pero cada resultado de un Horario o de un HTTP vuelve a entrar al
/// motor como un recorrido nuevo, así que un bloque cuya salida vuelve a él (o a uno anterior) no se ve como ciclo y el validador lo publica.
/// Sin tope, el procesador daría vueltas dentro de la unidad pasado el lease de 30 s, y el simulador sin fin (sin espera de red siquiera). El
/// mismo tope para los dos: el flujo de Clasic (Anexo B) encadena a lo sumo cinco efectos externos en un turno del cliente.
/// </summary>
public static class EngineReentries
{
    /// <summary>Las reentradas (HTTP más Horario) que admite una unidad del procesador o una llamada del simulador.</summary>
    public const int Max = 25;
}
