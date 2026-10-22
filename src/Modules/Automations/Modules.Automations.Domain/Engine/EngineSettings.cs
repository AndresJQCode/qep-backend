namespace Modules.Automations.Domain.Engine;

/// <summary>Lo que el motor lee del chatbot y no del flujo: la palabra que reinicia la corrida (D-I5) y los intentos ante una respuesta
/// inválida (D-M7), que un bloque puede pisar (D-C7).</summary>
public sealed record EngineSettings(string RestartKeyword, int MaxAttempts);
