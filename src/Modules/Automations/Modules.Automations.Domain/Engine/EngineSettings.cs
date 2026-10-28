namespace Modules.Automations.Domain.Engine;

/// <summary>Lo que el motor lee del chatbot y no del flujo: la palabra que reinicia la corrida (D-I5), los intentos ante una respuesta
/// inválida (D-M7), que un bloque puede pisar (D-C7), y qué hacer al agotarlos sin salida conectada (spec 2026-10-25 D-2):
/// <see cref="ExhaustedAction"/> y, si pasa a un asesor, a quién (<see cref="ExhaustedMemberId"/>; <c>null</c> = sin asignar).</summary>
public sealed record EngineSettings(
    string RestartKeyword, int MaxAttempts, AttemptsExhaustedAction ExhaustedAction = AttemptsExhaustedAction.Restart, Guid? ExhaustedMemberId = null);
