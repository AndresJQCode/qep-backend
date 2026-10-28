using System.Text.Json.Nodes;

namespace Modules.Automations.Domain.Engine;

/// <summary>Una variable que el bloque escribió: la respuesta guardada en su <c>saveTo</c> o lo que mezcló el mapeo de una llamada.</summary>
public sealed record StepWrite(string Name, JsonNode? Value);

/// <summary>
/// Spec 2026-10-25 D-E5 y D-E9: lo que el motor hizo en un bloque durante un paso, para reproducir la corrida. Se anota junto al
/// <see cref="TraceEntry"/> pero sin su tope de 200. <see cref="Seq"/> distingue la entrada al bloque (<see cref="StepSeqs.Entry"/>) de su
/// salida (<see cref="StepSeqs.Exit"/>) dentro del mismo <see cref="Step"/>. <see cref="SendIndexes"/> son posiciones en
/// <see cref="StepResult.Effects"/>. <see cref="FromNodeId"/> y <see cref="FromOutput"/> son la arista por la que se llegó, o
/// <c>null</c> si el registro no nació de seguir una salida (reenvío, pregunta de Política de datos, salida del bloque).
/// </summary>
public sealed record StepRecord(
    int Step,
    int Seq,
    string NodeId,
    string Kind,
    string Outcome,
    IReadOnlyList<StepWrite> Writes,
    IReadOnlyList<int> SendIndexes,
    bool Reset,
    string? FromNodeId,
    string? FromOutput);

/// <summary>Plan D-P1: 0 al entrar a un bloque, 1 al salir del bloque en que la corrida estaba parada.</summary>
public static class StepSeqs
{
    public const int Entry = 0;
    public const int Exit = 1;
}
